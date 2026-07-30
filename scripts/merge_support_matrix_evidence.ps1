[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(4, 4)]
    [string[]]$EntryPaths,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[1-9][0-9]*$")]
    [string]$WorkflowRunId,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$WorkflowRunAttempt,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release_common.ps1")

$RequiredTargets = @(
    "windows-11-24h2",
    "windows-11-25h2",
    "windows-server-2022-desktop",
    "windows-server-2025-desktop"
)
$EntriesByTarget = @{}
foreach ($EntryPath in $EntryPaths) {
    $ResolvedEntryPath = (
        Resolve-Path -LiteralPath $EntryPath
    ).Path
    try {
        $Entry = Get-Content `
            -LiteralPath $ResolvedEntryPath `
            -Raw `
            -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw "support_matrix_entry_invalid:$EntryPath"
    }
    if (
        $Entry.schemaVersion -cne "1.0" -or
        $Entry.targetId -notin $RequiredTargets -or
        -not $Entry.passed
    ) {
        throw "support_matrix_entry_metadata_invalid:$EntryPath"
    }
    if ($EntriesByTarget.ContainsKey($Entry.targetId)) {
        throw "support_matrix_entry_duplicate:$($Entry.targetId)"
    }
    $EntriesByTarget[$Entry.targetId] = [pscustomobject]@{
        Path = $ResolvedEntryPath
        Document = $Entry
    }
}
foreach ($TargetId in $RequiredTargets) {
    if (-not $EntriesByTarget.ContainsKey($TargetId)) {
        throw "support_matrix_entry_missing:$TargetId"
    }
}

$Reference = $EntriesByTarget[
    $RequiredTargets[0]
].Document
foreach ($TargetId in $RequiredTargets) {
    $Entry = $EntriesByTarget[$TargetId].Document
    foreach ($PropertyName in @(
        "productVersion",
        "gitCommit",
        "runtimeIdentifier",
        "sourceMsiSha256",
        "previousMsiSha256",
        "signatureMode",
        "signerSubject",
        "signerCertificateSha256"
    )) {
        if (
            [string]$Entry.$PropertyName -cne
                [string]$Reference.$PropertyName
        ) {
            throw (
                "support_matrix_entry_common_value_mismatch:" +
                "$TargetId`:$PropertyName"
            )
        }
    }
    if (
        [string]$Entry.sourceMsiSha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [string]$Entry.previousMsiSha256 -notmatch
            "^[0-9A-F]{64}$" -or
        [string]$Entry.signerCertificateSha256 -notmatch
            "^[0-9A-F]{64}$"
    ) {
        throw "support_matrix_entry_digest_invalid:$TargetId"
    }
    if (
        $Entry.lifecycleEvidence.currentMsiSha256 -cne
            $Entry.sourceMsiSha256 -or
        $Entry.lifecycleEvidence.previousMsiSha256 -cne
            $Entry.previousMsiSha256 -or
        -not $Entry.lifecycleEvidence.passed
    ) {
        throw "support_matrix_entry_lifecycle_binding_invalid:$TargetId"
    }
}

$Targets = [System.Collections.Generic.List[object]]::new()
foreach ($TargetId in $RequiredTargets) {
    $EntryRecord = $EntriesByTarget[$TargetId]
    $Entry = $EntryRecord.Document
    $EntryItem = Get-Item -LiteralPath $EntryRecord.Path
    $Targets.Add(
        [ordered]@{
            targetId = $TargetId
            testedAtUtc = [string]$Entry.testedAtUtc
            host = $Entry.host
            entryEvidence = [ordered]@{
                sha256 = Get-PerfMonitorSha256 `
                    -Path $EntryRecord.Path
                sizeBytes = $EntryItem.Length
            }
            lifecycleEvidence = $Entry.lifecycleEvidence
            passed = $true
        }
    )
}

$Evidence = [ordered]@{
    schemaVersion = "1.0"
    productVersion = [string]$Reference.productVersion
    gitCommit = [string]$Reference.gitCommit
    runtimeIdentifier = [string]$Reference.runtimeIdentifier
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceMsiSha256 = [string]$Reference.sourceMsiSha256
    previousMsiSha256 = [string]$Reference.previousMsiSha256
    signatureMode = [string]$Reference.signatureMode
    signerSubject = [string]$Reference.signerSubject
    signerCertificateSha256 = [string](
        $Reference.signerCertificateSha256
    )
    sourceWorkflow = [ordered]@{
        repository = $Repository
        runId = $WorkflowRunId
        runAttempt = $WorkflowRunAttempt
        workflowFile = ".github/workflows/support-matrix.yml"
    }
    targets = @($Targets)
    passed = $true
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$OutputDirectory = Split-Path -Parent $OutputPath
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force
$Json = $Evidence | ConvertTo-Json -Depth 16
Write-PerfMonitorUtf8NoBom -Path $OutputPath -Value $Json
$Json
