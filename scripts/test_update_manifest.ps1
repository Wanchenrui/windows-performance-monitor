[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$SignaturePath = "",

    [Parameter(Mandatory = $true)]
    [ValidateSet("test", "production")]
    [string]$AllowedMode,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSubject,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedSha256Thumbprint,

    [string]$InstallerPath = "",

    [string]$SbomPath = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release_common.ps1")
Add-Type -AssemblyName System.Security

$ManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
if (-not $SignaturePath) {
    $SignaturePath = $ManifestPath + ".p7s"
}
$SignaturePath = (Resolve-Path -LiteralPath $SignaturePath).Path
$ContentBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
$ContentInfo = New-Object `
    System.Security.Cryptography.Pkcs.ContentInfo `
    -ArgumentList (, $ContentBytes)
$Cms = New-Object `
    System.Security.Cryptography.Pkcs.SignedCms `
    -ArgumentList $ContentInfo, $true
$Cms.Decode([System.IO.File]::ReadAllBytes($SignaturePath))
$Cms.CheckSignature($AllowedMode -eq "test")
if ($Cms.SignerInfos.Count -ne 1) {
    throw "update_manifest_signer_count_invalid"
}
if (
    $Cms.SignerInfos[0].DigestAlgorithm.Value -cne
        "2.16.840.1.101.3.4.2.1"
) {
    throw "update_manifest_digest_algorithm_invalid"
}
$Certificate = $Cms.SignerInfos[0].Certificate
if ($null -eq $Certificate) {
    throw "update_manifest_certificate_missing"
}
$Sha256Thumbprint = Assert-PerfMonitorCodeSigningCertificate `
    -Certificate $Certificate `
    -Mode $AllowedMode `
    -ExpectedSubject $ExpectedSubject `
    -ExpectedSha256Thumbprint $ExpectedSha256Thumbprint
$Manifest = Get-Content -LiteralPath $ManifestPath -Raw |
    ConvertFrom-Json
$ExpectedChannel = if ($AllowedMode -eq "production") {
    "stable"
}
else {
    "test"
}
if (
    $Manifest.schemaVersion -ne "1.0" -or
    $Manifest.product -ne "PerfMonitor" -or
    [int]$Manifest.databaseSchemaVersion -ne 2 -or
    $Manifest.channel -cne $ExpectedChannel -or
    $Manifest.signer.mode -ne $AllowedMode -or
    $Manifest.signer.subject -cne $ExpectedSubject -or
    $Manifest.signer.certificateSha256 -cne $Sha256Thumbprint
) {
    throw "update_manifest_metadata_invalid"
}
if (
    $AllowedMode -eq "production" -and
    (
        -not [Uri]::IsWellFormedUriString(
            [string]$Manifest.installer.url,
            [UriKind]::Absolute
        ) -or
        -not [Uri]::IsWellFormedUriString(
            [string]$Manifest.sbom.url,
            [UriKind]::Absolute
        ) -or
        ([Uri]$Manifest.installer.url).Scheme -cne "https" -or
        ([Uri]$Manifest.sbom.url).Scheme -cne "https" -or
        ([Uri]$Manifest.installer.url).UserInfo -or
        ([Uri]$Manifest.sbom.url).UserInfo
    )
) {
    throw "update_manifest_production_url_invalid"
}
if ($InstallerPath) {
    $InstallerPath = Assert-PerfMonitorMsiFile -Path $InstallerPath
    if (
        $Manifest.installer.sha256 -cne (
            Get-PerfMonitorSha256 -Path $InstallerPath
        ) -or
        [Int64]$Manifest.installer.sizeBytes -ne (
            Get-Item -LiteralPath $InstallerPath
        ).Length
    ) {
        throw "update_manifest_installer_mismatch"
    }
}
if ($SbomPath) {
    $SbomPath = (Resolve-Path -LiteralPath $SbomPath).Path
    if (
        $Manifest.sbom.sha256 -cne (
            Get-PerfMonitorSha256 -Path $SbomPath
        ) -or
        [Int64]$Manifest.sbom.sizeBytes -ne (
            Get-Item -LiteralPath $SbomPath
        ).Length
    ) {
        throw "update_manifest_sbom_mismatch"
    }
}
[ordered]@{
    valid = $true
    mode = $AllowedMode
    subject = $Certificate.Subject
    certificateSha256 = $Sha256Thumbprint
    manifestSha256 = Get-PerfMonitorSha256 -Path $ManifestPath
    signatureSha256 = Get-PerfMonitorSha256 -Path $SignaturePath
} | ConvertTo-Json -Depth 4
