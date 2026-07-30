[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,

    [string]$ExpectedAgentSha256 = "",

    [string]$ExpectedProductVersion = "",

    [switch]$RequireRelease72Hour,

    [string]$BaselineCandidatePath = "",

    [string]$BudgetPath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

function Read-SoakJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    try {
        return Get-Content -LiteralPath $Path -Raw |
            ConvertFrom-Json
    }
    catch {
        throw $ErrorCode
    }
}

function ConvertTo-SoakTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    try {
        return [DateTimeOffset]::Parse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind
        )
    }
    catch {
        throw $ErrorCode
    }
}

$EvidencePath = (Resolve-Path -LiteralPath $EvidencePath).Path
$BudgetPath = if ($BudgetPath) {
    (Resolve-Path -LiteralPath $BudgetPath).Path
}
else {
    Join-Path $ProjectRoot "release\resource-budgets-v1.json"
}
$Evidence = Read-SoakJson `
    -Path $EvidencePath `
    -ErrorCode "soak_evidence_invalid"
$Budget = Read-SoakJson `
    -Path $BudgetPath `
    -ErrorCode "soak_budget_invalid"
if (
    $Budget.schemaVersion -cne "1.0" -or
    $null -eq $Budget.agent
) {
    throw "soak_budget_invalid"
}

$Candidate = $null
$CandidatePath = $null
$ValidationPlan = $null
if ($BaselineCandidatePath) {
    if (-not $RequireRelease72Hour) {
        throw "soak_candidate_requires_release_72h"
    }
    $CandidatePath = (
        Resolve-Path -LiteralPath $BaselineCandidatePath
    ).Path
    $Candidate = Read-SoakJson `
        -Path $CandidatePath `
        -ErrorCode "soak_candidate_invalid"
    if (
        $Candidate.schemaVersion -cne "1.0" -or
        $Candidate.runtimeIdentifier -cne "win-x64" -or
        $null -eq $Candidate.validationPlan -or
        [int]$Candidate.validationPlan.durationSeconds -ne
            72 * 60 * 60 -or
        [int]$Candidate.validationPlan.warmupSeconds -ne 5 -or
        [int](
            $Candidate.validationPlan.probeIntervalSeconds
        ) -ne 30 -or
        [int](
            $Candidate.validationPlan.snapshotPeriodSeconds
        ) -ne 300 -or
        [string]$Candidate.productVersion -notmatch
            "^[0-9]+\.[0-9]+\.[0-9]+$" -or
        [string]$Candidate.source.repository -notmatch
            "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$" -or
        [string]$Candidate.source.headCommit -notmatch
            "^[0-9a-f]{40}$" -or
        [string]$Candidate.source.workflowMergeCommit -notmatch
            "^[0-9a-f]{40}$" -or
        $Candidate.source.workflowFile -cne
            ".github/workflows/ci.yml" -or
        [Int64]$Candidate.source.workflowRunId -le 0 -or
        [Int64]$Candidate.artifact.id -le 0 -or
        [Int64]$Candidate.artifact.sizeBytes -le 0 -or
        [string]$Candidate.artifact.sha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [string]$Candidate.agent.sha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [string]$Candidate.agent.entryPointSha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [string]$Candidate.validator.sha256 -notmatch
            "^[0-9A-F]{64}$" -or
        $Candidate.agent.path -cne
            "agent/perf-monitor-agent.dll" -or
        $Candidate.agent.entryPointPath -cne
            "agent/perf-monitor-agent.exe" -or
        $Candidate.validator.path -cne
            "scripts/run_agent_soak.ps1" -or
        $Candidate.artifact.name -cne (
            "perf-monitor-agent-" +
            [string]$Candidate.source.workflowMergeCommit
        )
    ) {
        throw "soak_candidate_invalid"
    }
    if (
        $env:GITHUB_REPOSITORY -and
        $Candidate.source.repository -cne
            $env:GITHUB_REPOSITORY
    ) {
        throw "soak_candidate_repository_mismatch"
    }
    $ExpectedAgentSha256 =
        [string]$Candidate.agent.sha256
    $ExpectedProductVersion =
        [string]$Candidate.productVersion
    $ValidationPlan = $Candidate.validationPlan
    $CurrentHead = (
        & git -C $ProjectRoot rev-parse HEAD
    ).Trim()
    if (
        $LASTEXITCODE -ne 0 -or
        $CurrentHead -notmatch "^[0-9a-f]{40}$"
    ) {
        throw "soak_candidate_current_head_invalid"
    }
    $CandidateCommit = [string]$Candidate.source.headCommit
    $SavedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $null = & git -C $ProjectRoot cat-file `
            -e `
            ($CandidateCommit + "^{commit}") `
            2>&1
        $CommitExistsExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $SavedErrorActionPreference
    }
    if ($CommitExistsExitCode -ne 0) {
        throw "soak_candidate_source_commit_missing"
    }
    try {
        $ErrorActionPreference = "Continue"
        $null = & git -C $ProjectRoot merge-base `
            --is-ancestor `
            $CandidateCommit `
            $CurrentHead `
            2>&1
        $AncestorExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $SavedErrorActionPreference
    }
    if ($AncestorExitCode -ne 0) {
        throw "soak_candidate_not_in_current_history"
    }
}
elseif ($RequireRelease72Hour) {
    throw "soak_release_72h_candidate_required"
}

$ExpectedAgentSha256 = $ExpectedAgentSha256.Replace(
    " ",
    ""
).ToUpperInvariant()
if ($ExpectedAgentSha256 -notmatch "^[0-9A-F]{64}$") {
    throw "soak_expected_agent_sha256_invalid"
}
if (
    $ExpectedProductVersion -notmatch
        "^[0-9]+\.[0-9]+\.[0-9]+$"
) {
    throw "soak_expected_product_version_invalid"
}
if (
    $Evidence.contractVersion -cne "1.0" -or
    $Evidence.productVersion -cne $ExpectedProductVersion -or
    [string]$Evidence.agentSha256 -cne
        $ExpectedAgentSha256 -or
    -not [bool]$Evidence.passed -or
    -not [bool]$Evidence.snapshots.passed -or
    -not [bool]$Evidence.resources.passed
) {
    throw "soak_evidence_binding_invalid"
}

$ExpectedProfile = if ($RequireRelease72Hour) {
    "release-72h"
}
else {
    "accelerated-ci"
}
if ($Evidence.profile -cne $ExpectedProfile) {
    throw "soak_evidence_profile_invalid"
}
$StartedAt = ConvertTo-SoakTimestamp `
    -Value $Evidence.startedAtUtc `
    -ErrorCode "soak_started_at_invalid"
$CompletedAt = ConvertTo-SoakTimestamp `
    -Value $Evidence.completedAtUtc `
    -ErrorCode "soak_completed_at_invalid"
if ($CompletedAt -le $StartedAt) {
    throw "soak_elapsed_time_invalid"
}
if ($CompletedAt -gt [DateTimeOffset]::UtcNow) {
    throw "soak_completed_at_in_future"
}
$UtcElapsedSeconds = (
    $CompletedAt - $StartedAt
).TotalSeconds
if (
    [double]$Evidence.actualElapsedSeconds -le 0 -or
    [int]$Evidence.requestedDurationSeconds -le 0
) {
    throw "soak_elapsed_time_invalid"
}
if ($RequireRelease72Hour) {
    $RequiredSeconds = 72 * 60 * 60
    $MinimumPlanElapsedSeconds = (
        [int]$ValidationPlan.durationSeconds +
        [int]$ValidationPlan.warmupSeconds -
        2
    )
    if (
        [int]$Evidence.requestedDurationSeconds -ne
            [int]$ValidationPlan.durationSeconds -or
        [double]$Evidence.actualElapsedSeconds -lt
            $MinimumPlanElapsedSeconds -or
        $UtcElapsedSeconds -lt $MinimumPlanElapsedSeconds -or
        [int](
            $Evidence.release72HourGate.requiredDurationSeconds
        ) -ne $RequiredSeconds -or
        -not [bool](
            $Evidence.release72HourGate.actualWallClockPassed
        )
    ) {
        throw "soak_release_72h_wall_clock_invalid"
    }
    $ArtifactCreatedAt = ConvertTo-SoakTimestamp `
        -Value $Candidate.artifact.createdAtUtc `
        -ErrorCode "soak_candidate_artifact_time_invalid"
    if ($StartedAt -lt $ArtifactCreatedAt) {
        throw "soak_evidence_predates_candidate"
    }
}

if (
    [int]$Evidence.snapshots.count -lt 2 -or
    [Int64]$Evidence.snapshots.firstSequence -lt 1 -or
    [Int64]$Evidence.snapshots.lastSequence -le
        [Int64]$Evidence.snapshots.firstSequence -or
    [double]$Evidence.snapshots.maximumAllowed -lt 2 -or
    [double]$Evidence.snapshots.count -gt
        [double]$Evidence.snapshots.maximumAllowed
) {
    throw "soak_snapshot_evidence_invalid"
}
if ($RequireRelease72Hour) {
    # v1.0: allow one output boundary at shutdown, but require
    # essentially the full frozen 300-second snapshot cadence.
    $ExpectedMaximumSnapshots = [Math]::Ceiling(
        [int]$ValidationPlan.durationSeconds /
            [double]$ValidationPlan.snapshotPeriodSeconds
    ) + 3
    $MinimumSnapshotCount = [Math]::Max(
        2,
        [Math]::Floor(
            [int]$ValidationPlan.durationSeconds /
                [double]$ValidationPlan.snapshotPeriodSeconds
        ) - 1
    )
    if (
        [int]$Evidence.snapshots.count -lt
            $MinimumSnapshotCount -or
        [int]$Evidence.snapshots.maximumAllowed -ne
            $ExpectedMaximumSnapshots
    ) {
        throw "soak_snapshot_coverage_invalid"
    }
}

$AgentBudget = $Budget.agent
$Resources = $Evidence.resources
if (
    $null -eq $Resources.samples -or
    $null -eq $Resources.workingSet.peakMiB -or
    $null -eq $Resources.workingSet.limitMiB -or
    $null -eq $Resources.privateMemory.peakMiB -or
    $null -eq $Resources.privateMemory.limitMiB -or
    $null -eq $Resources.privateMemory.headMedianMiB -or
    $null -eq $Resources.privateMemory.tailMedianMiB -or
    $null -eq $Resources.privateMemory.retainedGrowthMiB -or
    $null -eq $Resources.privateMemory.growthLimitMiB -or
    $null -eq (
        $Resources.privateMemory.regressionSlopeMiBPerHour
    ) -or
    $null -eq $Resources.gcHeap.firstMiB -or
    $null -eq $Resources.gcHeap.lastMiB -or
    $null -eq $Resources.gcHeap.growthMiB -or
    $null -eq $Resources.gcHeap.growthLimitMiB -or
    $null -eq $Resources.cpuCoreEquivalentMeanPct -or
    $null -eq $Resources.cpuCoreEquivalentLimitPct -or
    $null -eq $Resources.handlePeak -or
    $null -eq $Resources.handleLimit -or
    $null -eq $Resources.handleHeadMedian -or
    $null -eq $Resources.handleTailMedian -or
    $null -eq $Resources.retainedHandleGrowth -or
    $null -eq $Resources.retainedHandleGrowthLimit -or
    $null -eq $Resources.threadPeak -or
    $null -eq $Resources.threadLimit
) {
    throw "soak_resource_evidence_incomplete"
}
if (
    [double]$Resources.workingSet.peakMiB -lt 0 -or
    [double]$Resources.workingSet.limitMiB -le 0 -or
    [double]$Resources.privateMemory.peakMiB -lt 0 -or
    [double]$Resources.privateMemory.limitMiB -le 0 -or
    [double]$Resources.privateMemory.headMedianMiB -lt 0 -or
    [double]$Resources.privateMemory.tailMedianMiB -lt 0 -or
    [double]$Resources.privateMemory.growthLimitMiB -lt 0 -or
    [double]$Resources.gcHeap.firstMiB -lt 0 -or
    [double]$Resources.gcHeap.lastMiB -lt 0 -or
    [double]$Resources.gcHeap.growthLimitMiB -lt 0 -or
    [double]$Resources.cpuCoreEquivalentMeanPct -lt 0 -or
    [double]$Resources.cpuCoreEquivalentLimitPct -le 0 -or
    [int]$Resources.handlePeak -lt 0 -or
    [int]$Resources.handleLimit -le 0 -or
    [double]$Resources.handleHeadMedian -lt 0 -or
    [double]$Resources.handleTailMedian -lt 0 -or
    [double]$Resources.retainedHandleGrowthLimit -lt 0 -or
    [int]$Resources.threadPeak -lt 0 -or
    [int]$Resources.threadLimit -le 0
) {
    throw "soak_resource_evidence_range_invalid"
}
if ($RequireRelease72Hour) {
    # v1.0: the outer PowerShell probe uses relative sleeps. Permit
    # at most one frozen snapshot window of accumulated scheduling
    # overhead; a longer unobserved interval fails closed.
    $IdealResourceSamples = [Math]::Floor(
        (
            [int]$ValidationPlan.durationSeconds -
            [int]$ValidationPlan.warmupSeconds
        ) /
            [double]$ValidationPlan.probeIntervalSeconds
    ) - 1
    $AllowedMissingResourceSamples = [Math]::Ceiling(
        [int]$ValidationPlan.snapshotPeriodSeconds /
            [double]$ValidationPlan.probeIntervalSeconds
    )
    $MinimumResourceSamples = [Math]::Max(
        3,
        $IdealResourceSamples -
            $AllowedMissingResourceSamples
    )
    if (
        [int]$Resources.samples -lt
            $MinimumResourceSamples
    ) {
        throw "soak_resource_sample_coverage_invalid"
    }
}
$LimitChecks = @(
    @(
        [double]$Resources.workingSet.limitMiB,
        [double]$AgentBudget.workingSetPeakMiB
    ),
    @(
        [double]$Resources.privateMemory.limitMiB,
        [double]$AgentBudget.privateMemoryPeakMiB
    ),
    @(
        [double]$Resources.privateMemory.growthLimitMiB,
        [double]$AgentBudget.retainedPrivateGrowthMiB
    ),
    @(
        [double]$Resources.gcHeap.growthLimitMiB,
        [double]$AgentBudget.gcHeapGrowthMiB
    ),
    @(
        [double]$Resources.cpuCoreEquivalentLimitPct,
        [double]$AgentBudget.cpuCoreEquivalentMeanPct
    ),
    @(
        [double]$Resources.handleLimit,
        [double]$AgentBudget.handlePeak
    ),
    @(
        [double]$Resources.retainedHandleGrowthLimit,
        [double]$AgentBudget.retainedHandleGrowth
    ),
    @(
        [double]$Resources.threadLimit,
        [double]$AgentBudget.threadPeak
    )
)
foreach ($LimitCheck in $LimitChecks) {
    if ($LimitCheck[0] -ne $LimitCheck[1]) {
        throw "soak_resource_limit_mismatch"
    }
}
if (
    [int]$Resources.samples -lt 3 -or
    [double]$Resources.workingSet.peakMiB -gt
        [double]$AgentBudget.workingSetPeakMiB -or
    [double]$Resources.privateMemory.peakMiB -gt
        [double]$AgentBudget.privateMemoryPeakMiB -or
    [double]$Resources.privateMemory.retainedGrowthMiB -gt
        [double]$AgentBudget.retainedPrivateGrowthMiB -or
    [double]$Resources.gcHeap.growthMiB -gt
        [double]$AgentBudget.gcHeapGrowthMiB -or
    [double]$Resources.cpuCoreEquivalentMeanPct -gt
        [double]$AgentBudget.cpuCoreEquivalentMeanPct -or
    [int]$Resources.handlePeak -gt
        [int]$AgentBudget.handlePeak -or
    [double]$Resources.retainedHandleGrowth -gt
        [double]$AgentBudget.retainedHandleGrowth -or
    [int]$Resources.threadPeak -gt
        [int]$AgentBudget.threadPeak
) {
    throw "soak_resource_budget_recalculation_failed"
}

# v1.0: return the hashes that bind the reviewed budgets, candidate,
# and measured evidence; callers may embed the same files in a manifest.
[ordered]@{
    valid = $true
    profile = $ExpectedProfile
    productVersion = $ExpectedProductVersion
    agentSha256 = $ExpectedAgentSha256
    evidenceSha256 = Get-PerfMonitorSha256 -Path $EvidencePath
    budgetSha256 = Get-PerfMonitorSha256 -Path $BudgetPath
    baselineCandidateSha256 = if ($CandidatePath) {
        Get-PerfMonitorSha256 -Path $CandidatePath
    }
    else {
        $null
    }
    sourceHeadCommit = if ($null -ne $Candidate) {
        [string]$Candidate.source.headCommit
    }
    else {
        $null
    }
} | ConvertTo-Json -Depth 5
