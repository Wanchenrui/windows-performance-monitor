[CmdletBinding()]
param(
    [ValidateSet("unsigned", "test", "production")]
    [string]$Mode = "unsigned",

    [string]$WixEulaId = "",

    [string]$Publisher = "",

    [string]$PayloadRoot = "",

    [string]$OutputDirectory = "",

    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+$")]
    [string]$ProductVersionOverride = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")
$ProjectPath = Join-Path `
    $ProjectRoot `
    "installer\PerfMonitor.Installer\PerfMonitor.Installer.wixproj"
if ($WixEulaId -cne "wix7") {
    throw (
        "wix7_eula_acceptance_required: review the WiX 7 " +
        "OSMF/EULA and explicitly pass -WixEulaId wix7"
    )
}
if (-not $PayloadRoot) {
    $PayloadRoot = Join-Path $ProjectRoot "dist"
}
$PayloadRoot = [System.IO.Path]::GetFullPath($PayloadRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path `
        $ProjectRoot `
        "dist\installer"
}
$OutputDirectory = [System.IO.Path]::GetFullPath(
    $OutputDirectory
)
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force

$BuildProperties = [xml](
    Get-Content -LiteralPath (
        Join-Path $ProjectRoot "Directory.Build.props"
    ) -Raw
)
if (-not $Publisher) {
    $CompanyNode = $BuildProperties.SelectSingleNode(
        "/Project/PropertyGroup/Company"
    )
    if ($null -ne $CompanyNode) {
        $Publisher = $CompanyNode.InnerText.Trim()
    }
}
if (
    [string]::IsNullOrWhiteSpace($Publisher) -or
    $Publisher.Length -gt 255 -or
    $Publisher -match "[;`r`n]"
) {
    throw "installer_publisher_invalid"
}
if (
    $Mode -eq "production" -and
    $Publisher -like "*Not Configured*"
) {
    throw "production_publisher_not_configured"
}
if (
    $Mode -eq "production" -and
    $ProductVersionOverride
) {
    throw "production_product_version_override_forbidden"
}
$Entrypoints = @(
    "agent\perf-monitor-agent.exe",
    "agent\provider-worker\perf-monitor-provider-worker.exe",
    "desktop\perf-monitor-desktop.exe",
    "broker\perf-monitor-broker.exe",
    "support\perf-monitor-support.exe"
)
foreach ($RelativePath in $Entrypoints) {
    $Path = Join-Path $PayloadRoot $RelativePath
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "installer_payload_missing:$RelativePath"
    }
    if ($Mode -eq "unsigned") {
        continue
    }
    $Signature = Get-AuthenticodeSignature -LiteralPath $Path
    $AllowedStatuses = if ($Mode -eq "production") {
        @("Valid")
    }
    else {
        @("Valid", "UnknownError")
    }
    if (
        $Signature.Status.ToString() -notin $AllowedStatuses -or
        $null -eq $Signature.SignerCertificate
    ) {
        throw "installer_payload_signature_invalid:$RelativePath"
    }
    if (
        $Mode -eq "production" -and
        $Signature.SignerCertificate.Subject -cne $Publisher
    ) {
        throw "installer_payload_publisher_mismatch:$RelativePath"
    }
}

$BuildArguments = @(
    "build",
    $ProjectPath,
    "--configuration",
    "Release",
    "--output",
    $OutputDirectory,
    "-p:AcceptEula=$WixEulaId",
    "-p:PayloadRoot=$PayloadRoot",
    "-p:PerfMonitorPublisher=$Publisher"
)
if ($ProductVersionOverride) {
    $BuildArguments += "-p:Version=$ProductVersionOverride"
}
& dotnet $BuildArguments | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "installer_build_failed"
}
$Msi = Get-ChildItem `
    -LiteralPath $OutputDirectory `
    -Filter "PerfMonitor-*-win-x64.msi" `
    -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $Msi) {
    throw "installer_output_missing"
}
$MsiPath = Assert-PerfMonitorMsiFile -Path $Msi.FullName

[ordered]@{
    path = $MsiPath
    sha256 = (
        Get-FileHash `
            -LiteralPath $MsiPath `
            -Algorithm SHA256
    ).Hash
    sizeBytes = $Msi.Length
    payloadSignatureMode = $Mode
    publisher = $Publisher
} | ConvertTo-Json
