[CmdletBinding()]
param(
    [string]$RuntimeRoot = 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ('artifacts\local-user-installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$packageRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $packageRoot) {
    throw "Preserve the existing package and choose a new output directory: $packageRoot"
}
$runtimeVersion = '10.0.10'
$targetFramework = 'net10.0-windows10.0.17763.0\win-x64'
$components = @(
    @{ Project = 'PerfMonitor.Agent'; Destination = 'agent'; EntryPoint = 'perf-monitor-agent.exe' },
    @{ Project = 'PerfMonitor.ProviderWorker'; Destination = 'agent\provider-worker'; EntryPoint = 'perf-monitor-provider-worker.exe' },
    @{ Project = 'PerfMonitor.Desktop'; Destination = 'desktop'; EntryPoint = 'perf-monitor-desktop.exe' },
    @{ Project = 'PerfMonitor.Support'; Destination = 'support'; EntryPoint = 'perf-monitor-support.exe' }
)
$helperRoot = Join-Path $PSScriptRoot 'local-install'
$helpers = @('Install-PerfMonitor.ps1', 'Install-PerfMonitor.cmd', 'Launch-PerfMonitor.ps1', 'Launch-PerfMonitor.vbs', 'Uninstall-PerfMonitor.ps1', 'Uninstall-PerfMonitor.cmd')
foreach ($helper in $helpers) {
    if (-not (Test-Path -LiteralPath (Join-Path $helperRoot $helper) -PathType Leaf)) {
        throw "Missing installer source: $helper"
    }
}
foreach ($component in $components) {
    $component.Source = Join-Path $projectRoot ('src\' + $component.Project + '\bin\Release\' + $targetFramework)
    if (-not (Test-Path -LiteralPath (Join-Path $component.Source $component.EntryPoint) -PathType Leaf)) {
        throw "Missing existing Release output: $($component.Source)"
    }
}
$runtimeItems = @('dotnet.exe', 'host', "shared\Microsoft.NETCore.App\$runtimeVersion", "shared\Microsoft.WindowsDesktop.App\$runtimeVersion", 'LICENSE.txt', 'ThirdPartyNotices.txt')
foreach ($relativePath in $runtimeItems) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot $relativePath))) {
        throw "Missing runtime file/directory: $relativePath"
    }
}

$payloadRoot = Join-Path $packageRoot 'payload'
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
$componentRecords = @()
foreach ($component in $components) {
    $destinationPath = Join-Path $payloadRoot $component.Destination
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    Get-ChildItem -LiteralPath $component.Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destinationPath -Recurse
    }
    $entryPoint = Get-Item -LiteralPath (Join-Path $destinationPath $component.EntryPoint)
    $componentRecords += [ordered]@{
        component = $component.Project
        productVersion = $entryPoint.VersionInfo.ProductVersion
        existingBuildTimeUtc = $entryPoint.LastWriteTimeUtc.ToString('o')
    }
}
$runtimeDestination = Join-Path $payloadRoot 'runtime'
foreach ($relativePath in $runtimeItems) {
    $destinationPath = Join-Path $runtimeDestination $relativePath
    New-Item -ItemType Directory -Path (Split-Path $destinationPath -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $RuntimeRoot $relativePath) -Destination $destinationPath -Recurse
}
foreach ($helper in $helpers) {
    $destinationRoot = if ($helper.StartsWith('Install-', [StringComparison]::Ordinal)) { $packageRoot } else { $payloadRoot }
    Copy-Item -LiteralPath (Join-Path $helperRoot $helper) -Destination (Join-Path $destinationRoot $helper)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\local-user-install.md') -Destination (Join-Path $packageRoot 'README.md')
[ordered]@{
    kind = 'local-user-development-candidate'
    version = '1.1.0'
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runtimeVersion = $runtimeVersion
    sourceOutputs = $componentRecords
    compiledDuringPreparation = $false
    testsRunDuringPreparation = $false
    fileHashesComputed = $false
    isReleaseMsi = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'package-info.json') -Encoding UTF8
Write-Output $packageRoot
