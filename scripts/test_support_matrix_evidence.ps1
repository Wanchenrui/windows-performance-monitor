[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+$")]
    [string]$ExpectedProductVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$ExpectedGitCommit
)

$ErrorActionPreference = "Stop"
$EvidencePath = (Resolve-Path -LiteralPath $EvidencePath).Path
try {
    $Evidence = Get-Content `
        -LiteralPath $EvidencePath `
        -Raw `
        -Encoding UTF8 |
        ConvertFrom-Json
}
catch {
    throw "support_matrix_evidence_invalid"
}

$RequiredTargets = [ordered]@{
    "windows-11-24h2" = [ordered]@{
        currentBuildNumber = "26100"
        installationType = "Client"
        displayVersion = "24H2"
    }
    "windows-11-25h2" = [ordered]@{
        currentBuildNumber = "26200"
        installationType = "Client"
        displayVersion = "25H2"
    }
    "windows-server-2022-desktop" = [ordered]@{
        currentBuildNumber = "20348"
        installationType = "Server"
        displayVersion = ""
    }
    "windows-server-2025-desktop" = [ordered]@{
        currentBuildNumber = "26100"
        installationType = "Server"
        displayVersion = ""
    }
}
if (
    $Evidence.schemaVersion -cne "1.0" -or
    $Evidence.productVersion -cne $ExpectedProductVersion -or
    $Evidence.gitCommit -cne $ExpectedGitCommit -or
    $Evidence.runtimeIdentifier -cne "win-x64" -or
    $Evidence.signatureMode -notin @("test", "production") -or
    [string]$Evidence.sourceMsiSha256 -notmatch
        "^[0-9A-F]{64}$" -or
    [string]$Evidence.previousMsiSha256 -notmatch
        "^[0-9A-F]{64}$" -or
    [string]$Evidence.signerCertificateSha256 -notmatch
        "^[0-9A-F]{64}$" -or
    [string]::IsNullOrWhiteSpace(
        [string]$Evidence.signerSubject
    ) -or
    -not $Evidence.passed
) {
    throw "support_matrix_evidence_metadata_invalid"
}
if (
    [string]$Evidence.sourceWorkflow.repository -notmatch
        "^[^/\s]+/[^/\s]+$" -or
    [string]$Evidence.sourceWorkflow.runId -notmatch
        "^[1-9][0-9]*$" -or
    [int]$Evidence.sourceWorkflow.runAttempt -lt 1 -or
    $Evidence.sourceWorkflow.workflowFile -cne
        ".github/workflows/support-matrix.yml"
) {
    throw "support_matrix_workflow_provenance_invalid"
}

$Targets = @($Evidence.targets)
if ($Targets.Count -ne $RequiredTargets.Count) {
    throw "support_matrix_target_count_invalid"
}
$SeenTargets = @{}
for ($Index = 0; $Index -lt $RequiredTargets.Count; $Index++) {
    $ExpectedTargetId = @($RequiredTargets.Keys)[$Index]
    $ExpectedHost = $RequiredTargets[$ExpectedTargetId]
    $Target = $Targets[$Index]
    if (
        $Target.targetId -cne $ExpectedTargetId -or
        $SeenTargets.ContainsKey($Target.targetId) -or
        -not $Target.passed
    ) {
        throw "support_matrix_target_order_or_result_invalid"
    }
    $SeenTargets[$Target.targetId] = $true
    if (
        $Target.host.osArchitecture -cne "X64" -or
        $Target.host.currentBuildNumber -cne
            $ExpectedHost.currentBuildNumber -or
        $Target.host.installationType -cne
            $ExpectedHost.installationType -or
        (
            $ExpectedHost.displayVersion -and
            $Target.host.displayVersion -cne
                $ExpectedHost.displayVersion
        )
    ) {
        throw "support_matrix_target_host_invalid:$ExpectedTargetId"
    }
    if (
        [string]$Target.entryEvidence.sha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [int64]$Target.entryEvidence.sizeBytes -lt 1 -or
        [string]$Target.lifecycleEvidence.sha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [int64]$Target.lifecycleEvidence.sizeBytes -lt 1 -or
        $Target.lifecycleEvidence.currentMsiSha256 -cne
            $Evidence.sourceMsiSha256 -or
        $Target.lifecycleEvidence.previousMsiSha256 -cne
            $Evidence.previousMsiSha256 -or
        -not $Target.lifecycleEvidence.passed
    ) {
        throw (
            "support_matrix_target_evidence_invalid:" +
            $ExpectedTargetId
        )
    }
    $StartedAt = [DateTimeOffset]::Parse(
        [string]$Target.lifecycleEvidence.startedAtUtc
    )
    $CompletedAt = [DateTimeOffset]::Parse(
        [string]$Target.lifecycleEvidence.completedAtUtc
    )
    $TestedAt = [DateTimeOffset]::Parse(
        [string]$Target.testedAtUtc
    )
    if (
        $CompletedAt -lt $StartedAt -or
        $TestedAt -lt $CompletedAt
    ) {
        throw "support_matrix_target_time_invalid:$ExpectedTargetId"
    }
}

[ordered]@{
    schemaVersion = "1.0"
    evidenceSha256 = (
        Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    productVersion = $Evidence.productVersion
    gitCommit = $Evidence.gitCommit
    sourceMsiSha256 = $Evidence.sourceMsiSha256
    targetCount = $Targets.Count
    passed = $true
} | ConvertTo-Json -Depth 4
