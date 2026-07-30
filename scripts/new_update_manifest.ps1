[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+$")]
    [string]$ProductVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$GitCommit,

    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerUrl,

    [Parameter(Mandatory = $true)]
    [string]$SbomPath,

    [Parameter(Mandatory = $true)]
    [string]$SbomUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{40}$")]
    [string]$CertificateSha1,

    [Parameter(Mandatory = $true)]
    [ValidateSet("test", "production")]
    [string]$Mode,

    [ValidateSet("CurrentUser", "LocalMachine")]
    [string]$StoreLocation = "CurrentUser",

    [string]$ExpectedSubject = "",

    [string]$ExpectedSha256Thumbprint = "",

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release_common.ps1")
Add-Type -AssemblyName System.Security

$InstallerPath = Assert-PerfMonitorMsiFile -Path $InstallerPath
$SbomPath = (Resolve-Path -LiteralPath $SbomPath).Path
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
function Assert-UpdateArtifactUri {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [bool]$RequireHttps
    )

    [Uri]$Parsed = $null
    if (
        $Value -match "[`r`n]" -or
        -not [Uri]::TryCreate(
            $Value,
            [UriKind]::Absolute,
            [ref]$Parsed
        ) -or
        [string]::IsNullOrWhiteSpace($Parsed.Host) -or
        $Parsed.UserInfo
    ) {
        throw "update_artifact_url_invalid"
    }
    if (
        $RequireHttps -and
        $Parsed.Scheme -cne "https"
    ) {
        throw "production_update_url_https_required"
    }
}

Assert-UpdateArtifactUri `
    -Value $InstallerUrl `
    -RequireHttps ($Mode -eq "production")
Assert-UpdateArtifactUri `
    -Value $SbomUrl `
    -RequireHttps ($Mode -eq "production")
if ($Mode -eq "production") {
    $Channel = "stable"
}
else {
    $Channel = "test"
}
$Certificate = Get-PerfMonitorSigningCertificate `
    -Sha1Thumbprint $CertificateSha1 `
    -StoreLocation $StoreLocation
$Sha256Thumbprint = Assert-PerfMonitorCodeSigningCertificate `
    -Certificate $Certificate `
    -Mode $Mode `
    -ExpectedSubject $ExpectedSubject `
    -ExpectedSha256Thumbprint $ExpectedSha256Thumbprint
$InstallerSignature = Get-AuthenticodeSignature `
    -LiteralPath $InstallerPath
$AllowedInstallerStatuses = if ($Mode -eq "production") {
    @("Valid")
}
else {
    @("Valid", "UnknownError")
}
if (
    $InstallerSignature.Status.ToString() -notin
        $AllowedInstallerStatuses -or
    $null -eq $InstallerSignature.SignerCertificate -or
    (
        Get-PerfMonitorCertificateSha256 `
            -Certificate $InstallerSignature.SignerCertificate
    ) -cne $Sha256Thumbprint
) {
    throw "update_installer_authenticode_invalid"
}
try {
    $Sbom = Get-Content -LiteralPath $SbomPath -Raw |
        ConvertFrom-Json
}
catch {
    throw "update_sbom_invalid"
}
if (
    $Sbom.bomFormat -cne "CycloneDX" -or
    $Sbom.specVersion -cne "1.6" -or
    @($Sbom.components).Count -eq 0
) {
    throw "update_sbom_incomplete"
}
$CurrentCommit = (& git -C (
    Split-Path -Parent $PSScriptRoot
) rev-parse HEAD).Trim()
if (
    $LASTEXITCODE -ne 0 -or
    $CurrentCommit -cne $GitCommit
) {
    throw "update_git_commit_mismatch"
}
$Manifest = [ordered]@{
    schemaVersion = "1.0"
    product = "PerfMonitor"
    channel = $Channel
    version = $ProductVersion
    publishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gitCommit = $GitCommit
    databaseSchemaVersion = 2
    support = [ordered]@{
        architecture = "x64"
        operatingSystems = @(
            "Windows 11 24H2"
            "Windows 11 25H2"
            "Windows Server 2022 Desktop Experience"
            "Windows Server 2025 Desktop Experience"
        )
    }
    installer = [ordered]@{
        url = $InstallerUrl
        sha256 = Get-PerfMonitorSha256 -Path $InstallerPath
        sizeBytes = (Get-Item -LiteralPath $InstallerPath).Length
    }
    sbom = [ordered]@{
        url = $SbomUrl
        sha256 = Get-PerfMonitorSha256 -Path $SbomPath
        sizeBytes = (Get-Item -LiteralPath $SbomPath).Length
    }
    signer = [ordered]@{
        mode = $Mode
        subject = $Certificate.Subject
        certificateSha256 = $Sha256Thumbprint
    }
}
$ManifestJson = $Manifest | ConvertTo-Json -Depth 8
$OutputDirectory = Split-Path -Parent $OutputPath
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force
Write-PerfMonitorUtf8NoBom `
    -Path $OutputPath `
    -Value $ManifestJson

$ContentBytes = [System.IO.File]::ReadAllBytes($OutputPath)
$ContentInfo = New-Object `
    System.Security.Cryptography.Pkcs.ContentInfo `
    -ArgumentList (, $ContentBytes)
$Cms = New-Object `
    System.Security.Cryptography.Pkcs.SignedCms `
    -ArgumentList $ContentInfo, $true
$Signer = New-Object `
    System.Security.Cryptography.Pkcs.CmsSigner `
    -ArgumentList $Certificate
$Signer.DigestAlgorithm = [System.Security.Cryptography.Oid]::new(
    "2.16.840.1.101.3.4.2.1",
    "SHA256"
)
$Signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$Cms.ComputeSignature($Signer, $true)
$SignaturePath = $OutputPath + ".p7s"
[System.IO.File]::WriteAllBytes(
    $SignaturePath,
    $Cms.Encode()
)
[ordered]@{
    manifestPath = $OutputPath
    signaturePath = $SignaturePath
    manifestSha256 = Get-PerfMonitorSha256 -Path $OutputPath
    signatureSha256 = Get-PerfMonitorSha256 -Path $SignaturePath
    signerCertificateSha256 = $Sha256Thumbprint
} | ConvertTo-Json -Depth 4
