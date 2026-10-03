Set-StrictMode -Version 2.0

function Get-PerfMonitorSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (
        Get-FileHash -LiteralPath $Path -Algorithm SHA256
    ).Hash.ToUpperInvariant()
}

function Assert-PerfMonitorMsiFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $ResolvedPath = (Resolve-Path -LiteralPath $Path).Path
    if (
        [System.IO.Path]::GetExtension($ResolvedPath) -cne ".msi"
    ) {
        throw "installer_file_extension_invalid"
    }

    [byte[]]$ExpectedHeader = @(
        0xD0,
        0xCF,
        0x11,
        0xE0,
        0xA1,
        0xB1,
        0x1A,
        0xE1
    )
    [byte[]]$ActualHeader = New-Object byte[] $ExpectedHeader.Length
    $Stream = [System.IO.File]::Open(
        $ResolvedPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )
    try {
        $BytesRead = $Stream.Read(
            $ActualHeader,
            0,
            $ActualHeader.Length
        )
    }
    finally {
        $Stream.Dispose()
    }
    if ($BytesRead -ne $ExpectedHeader.Length) {
        throw "installer_file_header_invalid"
    }
    for (
        $Index = 0;
        $Index -lt $ExpectedHeader.Length;
        $Index++
    ) {
        if ($ActualHeader[$Index] -ne $ExpectedHeader[$Index]) {
            throw "installer_file_header_invalid"
        }
    }

    return $ResolvedPath
}

function Get-PerfMonitorDependencyInputHashes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot,

        [switch]$RequireInstallerLock
    )

    $ResolvedRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
    $InstallerLockPath = Join-Path `
        $ResolvedRoot `
        "installer\PerfMonitor.Installer\packages.lock.json"
    if (
        $RequireInstallerLock -and
        -not (Test-Path -LiteralPath $InstallerLockPath)
    ) {
        throw "installer_dependency_lock_missing"
    }

    $InputPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($RelativePath in @(
        "PerfMonitor.slnx",
        "Directory.Build.props",
        "global.json",
        ".config\dotnet-tools.json",
        "requirements.txt",
        "requirements-dev.txt",
        "requirements-audit.txt",
        "release\vulnerability-waivers-v1.json"
    )) {
        $Path = Join-Path $ResolvedRoot $RelativePath
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "dependency_input_missing:$RelativePath"
        }
        $InputPaths.Add($Path)
    }

    $SearchRoots = @(
        (Join-Path $ResolvedRoot "src"),
        (Join-Path $ResolvedRoot "tests"),
        (Join-Path $ResolvedRoot "tools"),
        (Join-Path $ResolvedRoot "installer")
    )
    foreach ($Pattern in @(
        "*.csproj",
        "*.wixproj",
        "packages.lock.json"
    )) {
        foreach ($Path in Get-ChildItem `
            -LiteralPath $SearchRoots `
            -Filter $Pattern `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName) {
            $InputPaths.Add($Path)
        }
    }

    $RootPrefix = $ResolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    $Hashes = [ordered]@{}
    foreach ($InputPath in $InputPaths | Sort-Object -Unique) {
        $FullPath = [System.IO.Path]::GetFullPath($InputPath)
        if (-not $FullPath.StartsWith(
            $RootPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "dependency_input_outside_project:$FullPath"
        }
        $RelativePath = $FullPath.Substring(
            $ResolvedRoot.Length + 1
        ).Replace("\", "/")
        $Hashes[$RelativePath] = Get-PerfMonitorSha256 `
            -Path $FullPath
    }

    return $Hashes
}

function Get-PerfMonitorSupportMatrixHostEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet(
            "windows-11-24h2",
            "windows-11-25h2",
            "windows-server-2022-desktop",
            "windows-server-2025-desktop"
        )]
        [string]$TargetId
    )

    $CurrentVersion = Get-ItemProperty `
        -LiteralPath (
            "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
        )
    $HostEvidence = [pscustomobject][ordered]@{
        productName = [string]$CurrentVersion.ProductName
        displayVersion = [string]$CurrentVersion.DisplayVersion
        currentBuildNumber = [string](
            $CurrentVersion.CurrentBuildNumber
        )
        ubr = [int]$CurrentVersion.UBR
        installationType = [string](
            $CurrentVersion.InstallationType
        )
        editionId = [string]$CurrentVersion.EditionID
        osArchitecture = (
            [Runtime.InteropServices.RuntimeInformation]::
                OSArchitecture.ToString()
        )
    }
    foreach ($RequiredHostValue in @(
        "productName",
        "displayVersion",
        "currentBuildNumber",
        "installationType",
        "editionId"
    )) {
        if ([string]::IsNullOrWhiteSpace(
            [string]$HostEvidence.$RequiredHostValue
        )) {
            throw (
                "support_matrix_host_value_missing:" +
                $RequiredHostValue
            )
        }
    }
    if (
        -not [Environment]::Is64BitOperatingSystem -or
        $HostEvidence.osArchitecture -cne "X64"
    ) {
        throw "support_matrix_host_architecture_invalid"
    }

    $ExpectedHost = switch ($TargetId) {
        "windows-11-24h2" {
            [ordered]@{
                currentBuildNumber = "26100"
                installationType = "Client"
                displayVersion = "24H2"
            }
        }
        "windows-11-25h2" {
            [ordered]@{
                currentBuildNumber = "26200"
                installationType = "Client"
                displayVersion = "25H2"
            }
        }
        "windows-server-2022-desktop" {
            [ordered]@{
                currentBuildNumber = "20348"
                installationType = "Server"
                displayVersion = ""
            }
        }
        "windows-server-2025-desktop" {
            [ordered]@{
                currentBuildNumber = "26100"
                installationType = "Server"
                displayVersion = ""
            }
        }
    }
    foreach ($PropertyName in @(
        "currentBuildNumber",
        "installationType"
    )) {
        if (
            $HostEvidence.$PropertyName -cne
                $ExpectedHost[$PropertyName]
        ) {
            throw (
                "support_matrix_target_host_mismatch:" +
                $PropertyName
            )
        }
    }
    if (
        $ExpectedHost.displayVersion -and
        $HostEvidence.displayVersion -cne
            $ExpectedHost.displayVersion
    ) {
        throw "support_matrix_target_host_mismatch:displayVersion"
    }

    return $HostEvidence
}

function Get-PerfMonitorCertificateSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    $Sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString(
                $Sha256.ComputeHash($Certificate.RawData)
            ).Replace("-", "")
        )
    }
    finally {
        $Sha256.Dispose()
    }
}

function Get-PerfMonitorSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sha1Thumbprint,

        [ValidateSet("CurrentUser", "LocalMachine")]
        [string]$StoreLocation = "CurrentUser"
    )

    $Normalized = $Sha1Thumbprint.Replace(" ", "").ToUpperInvariant()
    if ($Normalized -notmatch "^[0-9A-F]{40}$") {
        throw "signing_certificate_sha1_invalid"
    }
    $StorePath = "Cert:\$StoreLocation\My"
    $Certificate = Get-ChildItem -LiteralPath $StorePath |
        Where-Object {
            $_.Thumbprint.ToUpperInvariant() -eq $Normalized
        } |
        Select-Object -First 1
    if ($null -eq $Certificate -or -not $Certificate.HasPrivateKey) {
        throw "signing_certificate_private_key_missing"
    }
    return $Certificate
}

function Assert-PerfMonitorCodeSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate,

        [Parameter(Mandatory = $true)]
        [ValidateSet("test", "production")]
        [string]$Mode,

        [string]$ExpectedSubject = "",

        [string]$ExpectedSha256Thumbprint = ""
    )

    $Now = [DateTimeOffset]::UtcNow
    if (
        $Now.UtcDateTime -lt $Certificate.NotBefore.ToUniversalTime() -or
        $Now.UtcDateTime -gt $Certificate.NotAfter.ToUniversalTime()
    ) {
        throw "signing_certificate_not_current"
    }
    $CodeSigningOid = "1.3.6.1.5.5.7.3.3"
    $HasCodeSigningEku = $false
    foreach ($Extension in $Certificate.Extensions) {
        if (
            $Extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
        ) {
            foreach ($Oid in $Extension.EnhancedKeyUsages) {
                if ($Oid.Value -eq $CodeSigningOid) {
                    $HasCodeSigningEku = $true
                }
            }
        }
    }
    if (-not $HasCodeSigningEku) {
        throw "signing_certificate_eku_invalid"
    }
    if (
        $ExpectedSubject -and
        $Certificate.Subject -cne $ExpectedSubject
    ) {
        throw "signing_certificate_subject_mismatch"
    }
    $Sha256Thumbprint = Get-PerfMonitorCertificateSha256 `
        -Certificate $Certificate
    if (
        $ExpectedSha256Thumbprint -and
        $Sha256Thumbprint -cne (
            $ExpectedSha256Thumbprint.Replace(
                " ",
                ""
            ).ToUpperInvariant()
        )
    ) {
        throw "signing_certificate_thumbprint_mismatch"
    }
    $IsSelfSigned = $Certificate.Subject -ceq $Certificate.Issuer
    if ($Mode -eq "production") {
        if ($IsSelfSigned) {
            throw "production_certificate_must_not_be_self_signed"
        }
        if (
            -not $ExpectedSubject -or
            -not $ExpectedSha256Thumbprint
        ) {
            throw "production_certificate_pin_required"
        }
    }
    elseif (-not $IsSelfSigned) {
        throw "test_certificate_must_be_self_signed"
    }

    return $Sha256Thumbprint
}

function Get-PerfMonitorSignTool {
    param(
        [string]$ExplicitPath = ""
    )

    if ($ExplicitPath) {
        $Candidate = (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    elseif ($env:PERFMONITOR_SIGNTOOL_PATH) {
        $Candidate = (
            Resolve-Path -LiteralPath $env:PERFMONITOR_SIGNTOOL_PATH
        ).Path
    }
    else {
        $NugetRoot = if ($env:NUGET_PACKAGES) {
            $env:NUGET_PACKAGES
        }
        else {
            Join-Path $env:USERPROFILE ".nuget\packages"
        }
        $Candidate = Join-Path `
            $NugetRoot `
            (
                "microsoft.windows.sdk.buildtools\" +
                "10.0.28000.2270\bin\10.0.28000.0\" +
                "x64\signtool.exe"
            )
    }
    if (-not $Candidate -or -not (Test-Path -LiteralPath $Candidate)) {
        throw "signtool_not_found"
    }
    $Signature = Get-AuthenticodeSignature -LiteralPath $Candidate
    if (
        $Signature.Status -ne "Valid" -or
        $Signature.SignerCertificate.Subject -notlike (
            "*Microsoft Corporation*"
        )
    ) {
        throw "signtool_signature_invalid"
    }
    return (Resolve-Path -LiteralPath $Candidate).Path
}

function Write-PerfMonitorUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $Encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Value, $Encoding)
}
