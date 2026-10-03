#requires -Version 7.0
<#
Finite synthetic WP04 scheduler measurement. Build the probe separately and run
only after receiving the shared workspace's exclusive execution window.
The fixture creates no Worker, Desktop, Broker, service or privileged collector.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$ProbePath,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 60)][int]$DurationSeconds = 3,
    [switch]$ReserveBasicGroups
)

$ErrorActionPreference = 'Stop'
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
$ProbePath = (Resolve-Path -LiteralPath $ProbePath).Path
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Never overwrite a prior probe run' }
$ParentDirectory = Split-Path $OutputDirectory
if (!(Test-Path -LiteralPath $ParentDirectory)) { $null = New-Item -ItemType Directory -Path $ParentDirectory }
$SourceFiles = Get-ChildItem -LiteralPath @(
    (Join-Path $SourceRoot 'src/PerfMonitor.Core'),
    (Join-Path $SourceRoot 'src/PerfMonitor.Contracts')) -File
$InputPaths = @('Directory.Build.props', 'global.json',
    'scripts/scheduler_fault_probe/Program.cs', 'scripts/scheduler_fault_probe/PerfMonitor.SchedulerFaultProbe.csproj',
    'scripts/run_scheduler_fault_probe.ps1') + @($SourceFiles |
    ForEach-Object { $_.FullName.Substring($SourceRoot.Length + 1).Replace('\', '/') })
$InputHashes = @($InputPaths | ForEach-Object {
    $Path = Join-Path $SourceRoot $_
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
})
$PayloadHashes = @(Get-ChildItem -LiteralPath (Split-Path $ProbePath) -File | ForEach-Object {
    [ordered]@{ path = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$Start = [Diagnostics.ProcessStartInfo]::new($DotnetPath)
$Start.UseShellExecute = $false
$Start.CreateNoWindow = $true
$Start.RedirectStandardOutput = $true
$Start.RedirectStandardError = $true
$Start.ArgumentList.Add($ProbePath)
$Start.ArgumentList.Add($OutputDirectory)
$Start.ArgumentList.Add($ReserveBasicGroups.IsPresent.ToString())
$Start.ArgumentList.Add($DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
$RuntimeRoot = Split-Path $DotnetPath
$Start.Environment['DOTNET_ROOT'] = $RuntimeRoot
$Start.Environment['DOTNET_ROOT_X64'] = $RuntimeRoot
$StartedUtc = [DateTime]::UtcNow
$Child = [Diagnostics.Process]::Start($Start)
$Stdout = $Child.StandardOutput.ReadToEndAsync()
$Stderr = $Child.StandardError.ReadToEndAsync()
try {
    if (!$Child.WaitForExit(($DurationSeconds + 20) * 1000)) {
        $Child.Kill($true)
        $Child.WaitForExit()
        throw 'Probe exceeded its finite run and shutdown allowance'
    }
    $ExitCode = $Child.ExitCode
    $Stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.log') -Encoding utf8
    $Stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.log') -Encoding utf8
    [ordered]@{
        schemaVersion = 1; startedUtc = $StartedUtc.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o')
        sourceRoot = $SourceRoot; sourceFileHashes = $InputHashes; payloadFileHashes = $PayloadHashes
        dotnetPath = $DotnetPath; dotnetSha256 = (Get-FileHash -LiteralPath $DotnetPath -Algorithm SHA256).Hash
        parameters = @{ durationSeconds = $DurationSeconds; reserveBasicGroups = $ReserveBasicGroups.IsPresent }
        exitCode = $ExitCode; probePid = $Child.Id; cleanup = 'owned probe exited'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8
    if ($ExitCode -ne 0) { throw "Probe exited $ExitCode; inspect this run's stderr.log" }
    Get-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.log')
}
finally { $Child.Dispose() }
