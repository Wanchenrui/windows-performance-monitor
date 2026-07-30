[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "windows-11-24h2",
        "windows-11-25h2",
        "windows-server-2022-desktop",
        "windows-server-2025-desktop"
    )]
    [string]$TargetId,

    [Parameter(Mandatory = $true)]
    [string]$BuildMetadataPath,

    [Parameter(Mandatory = $true)]
    [string]$LifecycleEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$CurrentMsiPath,

    [Parameter(Mandatory = $true)]
    [string]$PreviousMsiPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

$BuildMetadataPath = (
    Resolve-Path -LiteralPath $BuildMetadataPath
).Path
$LifecycleEvidencePath = (
    Resolve-Path -LiteralPath $LifecycleEvidencePath
).Path
$CurrentMsiPath = Assert-PerfMonitorMsiFile -Path $CurrentMsiPath
$PreviousMsiPath = Assert-PerfMonitorMsiFile -Path $PreviousMsiPath
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

function Read-MatrixJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw $ErrorCode
    }
}

$BuildMetadata = Read-MatrixJson `
    -Path $BuildMetadataPath `
    -ErrorCode "support_matrix_build_metadata_invalid"
$Lifecycle = Read-MatrixJson `
    -Path $LifecycleEvidencePath `
    -ErrorCode "support_matrix_lifecycle_evidence_invalid"

$BuildProperties = [xml](
    Get-Content -LiteralPath (
        Join-Path $ProjectRoot "Directory.Build.props"
    ) -Raw -Encoding UTF8
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "support_matrix_product_version_missing"
}
$ProductVersion = $VersionNode.InnerText.Trim()
$GitCommit = (& git rev-parse HEAD).Trim()
if (
    $LASTEXITCODE -ne 0 -or
    $GitCommit -notmatch "^[0-9a-f]{40}$"
) {
    throw "support_matrix_git_commit_invalid"
}

$CurrentMsiSha256 = Get-PerfMonitorSha256 -Path $CurrentMsiPath
$PreviousMsiSha256 = Get-PerfMonitorSha256 -Path $PreviousMsiPath
if (
    $BuildMetadata.schemaVersion -cne "1.0" -or
    $BuildMetadata.productVersion -cne $ProductVersion -or
    $BuildMetadata.gitCommit -cne $GitCommit -or
    $BuildMetadata.runtimeIdentifier -cne "win-x64" -or
    $BuildMetadata.signatureMode -notin @("test", "production") -or
    [string]::IsNullOrWhiteSpace(
        [string]$BuildMetadata.signerSubject
    ) -or
    [string]$BuildMetadata.signerCertificateSha256 -notmatch
        "^[0-9A-F]{64}$" -or
    $BuildMetadata.currentMsiSha256 -cne $CurrentMsiSha256 -or
    $BuildMetadata.previousMsiSha256 -cne $PreviousMsiSha256
) {
    throw "support_matrix_build_binding_invalid"
}

foreach ($PropertyName in @(
    "passed",
    "coreDefaultBrokerAbsent",
    "agentDesktopSqliteLifecyclePassed",
    "upgradePassed",
    "directDowngradeBlocked",
    "brokerServiceLocalSystemAuto",
    "brokerProgramDataAclPassed",
    "uninstallPreservedData",
    "reinstallPassed",
    "repairPassed",
    "controlledRollbackPassed"
)) {
    if (-not [bool]$Lifecycle.$PropertyName) {
        throw "support_matrix_lifecycle_failed:$PropertyName"
    }
}
if (
    $Lifecycle.schemaVersion -cne "1.0" -or
    $Lifecycle.currentMsiSha256 -cne $CurrentMsiSha256 -or
    $Lifecycle.previousMsiSha256 -cne $PreviousMsiSha256 -or
    $Lifecycle.signatureMode -cne
        $BuildMetadata.signatureMode -or
    $Lifecycle.signerSubject -cne
        $BuildMetadata.signerSubject -or
    $Lifecycle.signerCertificateSha256 -cne
        $BuildMetadata.signerCertificateSha256
) {
    throw "support_matrix_lifecycle_binding_invalid"
}

$HostEvidence = Get-PerfMonitorSupportMatrixHostEvidence `
    -TargetId $TargetId

$StartedAt = [DateTimeOffset]::Parse(
    [string]$Lifecycle.startedAtUtc
)
$CompletedAt = [DateTimeOffset]::Parse(
    [string]$Lifecycle.completedAtUtc
)
if ($CompletedAt -lt $StartedAt) {
    throw "support_matrix_lifecycle_time_invalid"
}

$LifecycleItem = Get-Item -LiteralPath $LifecycleEvidencePath
$Entry = [ordered]@{
    schemaVersion = "1.0"
    targetId = $TargetId
    testedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    productVersion = $ProductVersion
    gitCommit = $GitCommit
    runtimeIdentifier = "win-x64"
    sourceMsiSha256 = $CurrentMsiSha256
    previousMsiSha256 = $PreviousMsiSha256
    signatureMode = [string]$BuildMetadata.signatureMode
    signerSubject = [string]$BuildMetadata.signerSubject
    signerCertificateSha256 = [string](
        $BuildMetadata.signerCertificateSha256
    )
    host = $HostEvidence
    lifecycleEvidence = [ordered]@{
        sha256 = Get-PerfMonitorSha256 `
            -Path $LifecycleEvidencePath
        sizeBytes = $LifecycleItem.Length
        startedAtUtc = $StartedAt.ToString("O")
        completedAtUtc = $CompletedAt.ToString("O")
        currentMsiSha256 = $CurrentMsiSha256
        previousMsiSha256 = $PreviousMsiSha256
        passed = $true
    }
    passed = $true
}
$OutputDirectory = Split-Path -Parent $OutputPath
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force
$Json = $Entry | ConvertTo-Json -Depth 12
Write-PerfMonitorUtf8NoBom -Path $OutputPath -Value $Json
$Json
