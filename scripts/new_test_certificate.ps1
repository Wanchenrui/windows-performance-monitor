[CmdletBinding()]
param(
    [string]$Subject = "CN=PerfMonitor CI Test",

    [string]$OutputDirectory = "",

    [string]$PasswordEnvironmentVariable = (
        "PERFMONITOR_TEST_CERT_PASSWORD"
    ),

    [ValidateRange(1, 168)]
    [int]$ValidHours = 24,

    [switch]$InstallTrust
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path `
        $ProjectRoot `
        "artifacts\test-signing"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$Password = [Environment]::GetEnvironmentVariable(
    $PasswordEnvironmentVariable
)
if ([string]::IsNullOrEmpty($Password)) {
    throw "test_certificate_password_missing"
}
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force

$CertificateClock = Get-Date
$Certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotBefore $CertificateClock.AddMinutes(-5) `
    -NotAfter $CertificateClock.AddHours($ValidHours)
$SecurePassword = ConvertTo-SecureString `
    -String $Password `
    -AsPlainText `
    -Force
$PfxPath = Join-Path $OutputDirectory "perf-monitor-ci-test.pfx"
$CerPath = Join-Path $OutputDirectory "perf-monitor-ci-test.cer"
$null = Export-PfxCertificate `
    -Cert $Certificate `
    -FilePath $PfxPath `
    -Password $SecurePassword `
    -CryptoAlgorithmOption AES256_SHA256
$null = Export-Certificate `
    -Cert $Certificate `
    -FilePath $CerPath `
    -Type CERT

if ($InstallTrust) {
    $null = Import-Certificate `
        -FilePath $CerPath `
        -CertStoreLocation "Cert:\CurrentUser\Root"
    $null = Import-Certificate `
        -FilePath $CerPath `
        -CertStoreLocation "Cert:\CurrentUser\TrustedPublisher"
}

$Sha256 = Assert-PerfMonitorCodeSigningCertificate `
    -Certificate $Certificate `
    -Mode test `
    -ExpectedSubject $Subject
$Result = [ordered]@{
    mode = "test"
    subject = $Certificate.Subject
    issuer = $Certificate.Issuer
    sha1Thumbprint = $Certificate.Thumbprint.ToUpperInvariant()
    sha256Thumbprint = $Sha256
    notBeforeUtc = $Certificate.NotBefore.ToUniversalTime().ToString("O")
    notAfterUtc = $Certificate.NotAfter.ToUniversalTime().ToString("O")
    pfxPath = $PfxPath
    cerPath = $CerPath
    trustInstalled = [bool]$InstallTrust
}
$Result | ConvertTo-Json -Depth 4
