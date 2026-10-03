[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,

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

    [string]$TimestampUrl = "",

    [string]$SignToolPath = "",

    [string]$EvidencePath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

$Certificate = Get-PerfMonitorSigningCertificate `
    -Sha1Thumbprint $CertificateSha1 `
    -StoreLocation $StoreLocation
$Sha256Thumbprint = Assert-PerfMonitorCodeSigningCertificate `
    -Certificate $Certificate `
    -Mode $Mode `
    -ExpectedSubject $ExpectedSubject `
    -ExpectedSha256Thumbprint $ExpectedSha256Thumbprint
if ($Mode -eq "production") {
    [Uri]$TimestampUri = $null
    if (
        $TimestampUrl -match "[`r`n]" -or
        -not [Uri]::TryCreate(
            $TimestampUrl,
            [UriKind]::Absolute,
            [ref]$TimestampUri
        ) -or
        $TimestampUri.Scheme -cne "https" -or
        [string]::IsNullOrWhiteSpace($TimestampUri.Host) -or
        $TimestampUri.UserInfo
    ) {
        throw "production_timestamp_https_required"
    }
}
elseif ($TimestampUrl) {
    throw "test_signing_must_not_use_production_timestamp"
}

$SignTool = Get-PerfMonitorSignTool -ExplicitPath $SignToolPath
$ResolvedPaths = @(
    $Paths |
        ForEach-Object {
            (Resolve-Path -LiteralPath $_).Path
        } |
        Sort-Object -Unique
)
if ($ResolvedPaths.Count -eq 0) {
    throw "signing_files_required"
}
$EvidenceFiles = [System.Collections.Generic.List[object]]::new()
foreach ($Path in $ResolvedPaths) {
    $Extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($Extension -notin @(".exe", ".dll", ".msi")) {
        throw "signing_file_type_not_allowed"
    }
    $Arguments = @(
        "sign",
        "/v",
        "/fd",
        "SHA256",
        "/sha1",
        $Certificate.Thumbprint,
        "/s",
        "My"
    )
    if ($StoreLocation -eq "LocalMachine") {
        $Arguments += "/sm"
    }
    if ($Mode -eq "production") {
        $Arguments += @(
            "/tr",
            $TimestampUrl,
            "/td",
            "SHA256"
        )
    }
    $Arguments += $Path
    & $SignTool $Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "authenticode_sign_failed"
    }

    & $SignTool verify /pa /all /v $Path
    $VerifyExitCode = $LASTEXITCODE
    $Signature = Get-AuthenticodeSignature -LiteralPath $Path
    $AllowedStatuses = if ($Mode -eq "production") {
        @("Valid")
    }
    else {
        # Test certificates may be temporarily trusted by the release
        # harness, but local validation also permits an intact self-signed
        # signature with an untrusted chain. HashMismatch and NotSigned
        # remain forbidden.
        @("Valid", "UnknownError")
    }
    if ($Signature.Status.ToString() -notin $AllowedStatuses) {
        throw "authenticode_verify_failed"
    }
    if (
        $null -eq $Signature.SignerCertificate -or
        $Signature.SignerCertificate.Thumbprint -cne (
            $Certificate.Thumbprint
        )
    ) {
        throw "authenticode_signer_mismatch"
    }
    if ($Mode -eq "production" -and $VerifyExitCode -ne 0) {
        throw "authenticode_trust_verify_failed"
    }
    $RelativePath = if (
        $Path.StartsWith(
            $ProjectRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        $Path.Substring($ProjectRoot.Length + 1).Replace("\", "/")
    }
    else {
        [System.IO.Path]::GetFileName($Path)
    }
    $EvidenceFiles.Add(
        [ordered]@{
            path = $RelativePath
            sha256 = Get-PerfMonitorSha256 -Path $Path
            sizeBytes = (Get-Item -LiteralPath $Path).Length
        }
    )
}

$Evidence = [ordered]@{
    schemaVersion = "1.0"
    signatureMode = $Mode
    subject = $Certificate.Subject
    issuer = $Certificate.Issuer
    certificateSha1 = $Certificate.Thumbprint.ToUpperInvariant()
    certificateSha256 = $Sha256Thumbprint
    timestampUrl = if ($Mode -eq "production") {
        $TimestampUrl
    }
    else {
        $null
    }
    signedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    files = $EvidenceFiles
}
$EvidenceJson = $Evidence | ConvertTo-Json -Depth 6
if ($EvidencePath) {
    $EvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    $EvidenceDirectory = Split-Path -Parent $EvidencePath
    $null = New-Item `
        -ItemType Directory `
        -Path $EvidenceDirectory `
        -Force
    Write-PerfMonitorUtf8NoBom `
        -Path $EvidencePath `
        -Value $EvidenceJson
}
$EvidenceJson
