#requires -Version 5.1
<#
Development measurements only. No resource-budget or release-72h verdict.
Each invocation owns a fresh payload/data directory and only its child processes.
Run modes serially; do not build or run other measurements concurrently.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentPath,
    [string]$DesktopPath = '',
    [string]$WorkerPath = '',
    [Parameter(Mandatory)][string]$PythonPath,
    [string]$DotnetRoot = '',
    [ValidateSet('agent-only', 'desktop-visible', 'desktop-hidden')]
    [string]$Mode = 'agent-only',
    [ValidateSet('workerDeployed', 'hardwareUnavailableDevelopmentProfile')]
    [string]$HardwareProfile = 'workerDeployed',
    [ValidateRange(15, 3600)][int]$DurationSeconds = 180,
    [ValidateRange(0, 120)][int]$WarmupSeconds = 10,
    [ValidateRange(250, 5000)][int]$ProbeIntervalMilliseconds = 1000,
    [string]$OutputDirectory = '',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'release_common.ps1')
$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
$PythonPath = (Resolve-Path -LiteralPath $PythonPath).Path
if ($Mode -ne 'agent-only') { $DesktopPath = (Resolve-Path -LiteralPath $DesktopPath).Path }
if ($WorkerPath) { $WorkerPath = (Resolve-Path -LiteralPath $WorkerPath).Path }
if ($DotnetRoot) {
    $DotnetRoot = (Resolve-Path -LiteralPath $DotnetRoot).Path
    if (!(Test-Path -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe'))) { throw 'DotnetRoot requires dotnet.exe' }
}
$SourceWorker = Join-Path (Split-Path $AgentPath) 'provider-worker/perf-monitor-provider-worker.exe'
if ($HardwareProfile -eq 'workerDeployed' -and !$WorkerPath -and !(Test-Path -LiteralPath $SourceWorker)) {
    throw 'workerDeployed requires WorkerPath or the existing standard provider-worker payload'
}
if ($HardwareProfile -eq 'hardwareUnavailableDevelopmentProfile' -and ($WorkerPath -or (Test-Path -LiteralPath $SourceWorker))) {
    throw 'Do not remove a deployed Worker to manufacture a hardware-unavailable profile'
}
$Existing = @(Get-Process -Name 'perf-monitor-agent', 'perf-monitor-desktop', 'perf-monitor-provider-worker', 'perf-monitor-broker' -ErrorAction SilentlyContinue)
if ($Existing.Count) { throw 'Existing PerfMonitor product process; stop the prior session before measuring' }
$Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $Pipe = '\\.\pipe\PerfMonitor.' + $Identity.User.Value + '.agent' }
finally { $Identity.Dispose() }
if ([IO.Directory]::GetFiles('\\.\pipe\') -contains $Pipe) { throw 'Existing current-user Agent pipe; measurement refused' }

function Get-Artifact([string]$Path) {
    $Item = Get-Item -LiteralPath $Path
    [ordered]@{ path = $Path; sizeBytes = $Item.Length; sha256 = Get-PerfMonitorSha256 $Path;
        fileVersion = $Item.VersionInfo.FileVersion; productVersion = $Item.VersionInfo.ProductVersion }
}
$InputArtifacts = @($AgentPath, [IO.Path]::ChangeExtension($AgentPath, '.dll'), $PythonPath,
    (Join-Path $PSScriptRoot 'run_product_baseline.ps1'), (Join-Path $PSScriptRoot 'read_baseline_history.py'))
if ($Mode -ne 'agent-only') { $InputArtifacts += @($DesktopPath, [IO.Path]::ChangeExtension($DesktopPath, '.dll')) }
if ($WorkerPath) { $InputArtifacts += @($WorkerPath, [IO.Path]::ChangeExtension($WorkerPath, '.dll')) }
elseif (Test-Path -LiteralPath $SourceWorker) { $InputArtifacts += @($SourceWorker, [IO.Path]::ChangeExtension($SourceWorker, '.dll')) }
$InputHashes = @($InputArtifacts | ForEach-Object { Get-Artifact $_ })
$SourceHead = (& git -C $ProjectRoot rev-parse HEAD).Trim()
$ExpectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $ProjectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
foreach ($Artifact in $InputHashes) {
    if ($Artifact.path -match 'perf-monitor-(agent|desktop|provider-worker)\.(exe|dll)$' -and
        $Artifact.productVersion -cne ($ExpectedVersion + '+' + $SourceHead)) {
        throw 'Product artifact source-version metadata does not match the current HEAD; build the final isolated outputs first'
    }
}
if ($CheckOnly) {
    [ordered]@{ mode = $Mode; hardwareProfile = $HardwareProfile; artifacts = $InputHashes;
        hiddenModeRequiresProductStartHidden = $Mode -eq 'desktop-hidden'; durationSeconds = $DurationSeconds;
        warmupSeconds = $WarmupSeconds; probeIntervalMilliseconds = $ProbeIntervalMilliseconds } | ConvertTo-Json -Depth 6
    return
}
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $ProjectRoot ('artifacts/baseline/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-wp00-' + $Mode + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists; never overwrite a prior run' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$Payload = Join-Path $OutputDirectory 'payload'
$Data = Join-Path $OutputDirectory 'data'
$null = New-Item -ItemType Directory -Path $Payload, $Data
function Copy-Payload([string]$Path, [string]$Target) {
    $null = New-Item -ItemType Directory -Path $Target
    Get-ChildItem -LiteralPath (Split-Path $Path) -Force | Copy-Item -Destination $Target -Recurse
    return Join-Path $Target (Split-Path $Path -Leaf)
}
$RunAgent = Copy-Payload $AgentPath (Join-Path $Payload 'agent')
$RunDesktop = if ($Mode -ne 'agent-only') { Copy-Payload $DesktopPath (Join-Path $Payload 'desktop') } else { $null }
if ($WorkerPath) {
    $TargetWorker = Join-Path $Payload 'agent/provider-worker'
    if (Test-Path -LiteralPath $TargetWorker) { throw 'WorkerPath and packaged provider-worker are mutually exclusive' }
    $null = Copy-Payload $WorkerPath $TargetWorker
}
# Inspect only owned window state and power; no ShowWindow/UIA/input injection.
if (-not ('PerfMonitorBaseline.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace PerfMonitorBaseline {
    public static class Native {
        public delegate bool WindowCallback(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback cb, IntPtr p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        public static IntPtr Find(uint pid) {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, p) => { uint id; GetWindowThreadProcessId(h, out id); if (id == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
            return found;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Power { public byte Ac, Flag, Percent, Reserved; public uint Life, FullLife; }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out Power power);
    }
}
'@
}
$Power = [PerfMonitorBaseline.Native+Power]::new()
$PowerKnown = [PerfMonitorBaseline.Native]::GetSystemPowerStatus([ref]$Power)
$Os = Get-CimInstance Win32_OperatingSystem
$Computer = Get-CimInstance Win32_ComputerSystem
$Processors = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
$SourceStatus = @(& git -C $ProjectRoot status --porcelain)
$SourceHashes = @(& git -C $ProjectRoot ls-files --cached --others --exclude-standard -- src Directory.Build.props global.json | ForEach-Object {
    [ordered]@{ path = $_; sha256 = Get-PerfMonitorSha256 (Join-Path $ProjectRoot $_) }
})
$RunArtifacts = @(Get-ChildItem -LiteralPath $Payload -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($Payload.Length + 1).Replace('\', '/'); sizeBytes = $_.Length; sha256 = Get-PerfMonitorSha256 $_.FullName }
})
$Children = [Collections.Generic.List[object]]::new()
$Processes = [Collections.Generic.List[object]]::new()
$Rows = [Collections.Generic.List[object]]::new()
$SetRows = [Collections.Generic.List[object]]::new()
$Timer = [Diagnostics.Stopwatch]::new()
$Evidence = [ordered]@{ schemaVersion = 1; purpose = 'short local development baseline; no budget or 72-hour verdict';
    mode = $Mode; hardwareProfile = $HardwareProfile; brokerStarted = $false; actionsEnabled = $false;
    source = [ordered]@{ head = $SourceHead; workingTreeDirty = $SourceStatus.Count -gt 0; status = $SourceStatus; inputFileHashes = $SourceHashes };
    environment = [ordered]@{ os = $Os.Caption; version = $Os.Version; build = $Os.BuildNumber; architecture = $Os.OSArchitecture;
        logicalProcessors = [int]$Computer.NumberOfLogicalProcessors; physicalMemoryBytes = [long]$Computer.TotalPhysicalMemory; cpu = $Processors;
        powerStatusAvailable = $PowerKnown; acLineStatus = [int]$Power.Ac; batteryFlags = [int]$Power.Flag;
        observer = 'PowerShell + CIM + Process counters; observer overhead is excluded from product totals' };
    parameters = [ordered]@{ durationSeconds = $DurationSeconds; warmupSeconds = $WarmupSeconds; probeIntervalMilliseconds = $ProbeIntervalMilliseconds;
        dotnetRoot = $DotnetRoot; outputDirectory = $OutputDirectory; outputSerializationEnabled = $false; ipcEnabled = $true };
    inputArtifacts = $InputHashes; runningPayloadFiles = $RunArtifacts; processes = $Processes;
    workerDiscoveryErrors = 0; skippedProbeSlots = 0; storageBeforeShutdown = $null;
    limitations = @('Polling can miss short-lived Worker processes and CPU intervals before first observation', 'Working-set sums may double-count shared pages',
        'DB/WAL length changes are file-size observations, not physical I/O bytes or logical-row write amplification',
        'Ambient user load is uncontrolled; no synthetic process-count/high-load experiment', 'Visible mode covers the default compact window only',
        'No production support-matrix, actual actions, task benefits or long-soak conclusions'); completed = $false }

function Add-ObservedProcess([Diagnostics.Process]$Process, [string]$Kind, [string]$Path, [bool]$RootProcess) {
    $null = $Process.Handle # Retain the kernel handle; never clean up by a broad name or recycled PID.
    $Record = [ordered]@{ kind = $Kind; pid = $Process.Id; startedUtc = $Process.StartTime.ToUniversalTime().ToString('o'); path = $Path; rootProcess = $RootProcess; cleanup = 'pending' }
    $Child = [pscustomobject]@{ Process = $Process; Record = $Record; LastCpu = $null; SeenExited = $false; Streams = @(); Copies = @() }
    $Children.Add($Child); $Processes.Add($Record)
    return $Child
}
function Start-Child([string]$Kind, [string]$Path, [string[]]$Arguments, [bool]$Hidden) {
    $StartInfo = [Diagnostics.ProcessStartInfo]::new($Path)
    # Same Windows argv quoting as run_agent_soak.ps1; compatible with PS 5.1.
    $StartInfo.Arguments = ($Arguments | ForEach-Object {
        $Escaped = [Regex]::Replace($_, '(\\*)"', '$1$1\"')
        $Escaped = [Regex]::Replace($Escaped, '(\\+)$', '$1$1')
        '"' + $Escaped + '"'
    }) -join ' '
    $StartInfo.UseShellExecute = $false; $StartInfo.CreateNoWindow = $Hidden
    $StartInfo.WindowStyle = if ($Hidden) { [Diagnostics.ProcessWindowStyle]::Hidden } else { [Diagnostics.ProcessWindowStyle]::Normal }
    $StartInfo.WorkingDirectory = Split-Path $Path
    $StartInfo.RedirectStandardOutput = $true; $StartInfo.RedirectStandardError = $true
    if ($DotnetRoot) { $StartInfo.EnvironmentVariables['DOTNET_ROOT'] = $DotnetRoot; $StartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $DotnetRoot }
    $Process = [Diagnostics.Process]::new(); $Process.StartInfo = $StartInfo
    if (!$Process.Start()) { throw "Cannot start $Kind" }
    $Child = Add-ObservedProcess $Process $Kind $Path $true
    $Child.Record.arguments = $Arguments
    foreach ($Name in @('stdout', 'stderr')) { $Child.Streams += [IO.File]::Create((Join-Path $OutputDirectory "$Kind.$Name.log")) }
    $Child.Copies = @($Process.StandardOutput.BaseStream.CopyToAsync($Child.Streams[0]), $Process.StandardError.BaseStream.CopyToAsync($Child.Streams[1]))
    return $Process
}
function Get-StorageSizes {
    $Sizes = [ordered]@{}
    foreach ($File in @(
        @{ key = 'dbBytes'; suffix = '' },
        @{ key = 'walBytes'; suffix = '-wal' },
        @{ key = 'shmBytes'; suffix = '-shm' }
    )) {
        $Path = Join-Path $Data ('history-v1.db' + $File.suffix)
        $Sizes[$File.key] = if (Test-Path -LiteralPath $Path) { [long](Get-Item -LiteralPath $Path).Length } else { 0L }
    }
    return $Sizes
}
$WarmStorage = $null
try {
    $Evidence.startedUtc = [DateTimeOffset]::UtcNow.ToString('o'); $Timer.Start()
    $Agent = Start-Child 'agent' $RunAgent @('--quiet', '--warmup-seconds', '0', '--duration-seconds', [string]($WarmupSeconds + $DurationSeconds + 5), '--data-directory', $Data) $true
    $Desktop = $null
    if ($Mode -ne 'agent-only') {
        $DesktopArgs = if ($Mode -eq 'desktop-hidden') { @('--start-hidden') } else { @() }
        $Desktop = Start-Child 'desktop' $RunDesktop $DesktopArgs ($Mode -eq 'desktop-hidden')
    }
    $PreviousElapsed = $null; $NextProbe = 0.0; $WarmStorage = $null; $PreviousStorage = $null; $VisibilityMismatch = 0
    while ($Timer.Elapsed.TotalSeconds -lt ($WarmupSeconds + $DurationSeconds)) {
        $Now = $Timer.Elapsed.TotalSeconds
        if ($Now -lt $NextProbe) { Start-Sleep -Milliseconds ([int][Math]::Min(100.0, [double](1000 * ($NextProbe - $Now)))); continue }
        $Agent.Refresh()
        if ($Agent.HasExited) { throw 'Agent exited before the measurement interval finished' }
        if ($Desktop) { $Desktop.Refresh(); if ($Desktop.HasExited) { throw 'Desktop exited before the measurement interval finished' } }
        $DiscoverStarted = $Timer.Elapsed.TotalSeconds
        $DiscoveryComplete = $true
        $Workers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Agent.Id) AND Name='perf-monitor-provider-worker.exe'")
        foreach ($Worker in $Workers) {
            if (@($Children | Where-Object { $_.Record.pid -eq [int]$Worker.ProcessId }).Count) { continue }
            $Owned = $null
            try {
                $Owned = [Diagnostics.Process]::GetProcessById([int]$Worker.ProcessId)
                $null = $Owned.Handle
                $ExpectedWorkerPath = [IO.Path]::GetFullPath((Join-Path $Payload 'agent/provider-worker/perf-monitor-provider-worker.exe'))
                $CimStarted = ([DateTime]$Worker.CreationDate).ToUniversalTime()
                $StartDifferenceTicks = [Math]::Abs(($Owned.StartTime.ToUniversalTime() - $CimStarted).Ticks)
                if ($Owned.HasExited -or $StartDifferenceTicks -gt 10 -or $Owned.StartTime -lt $Agent.StartTime -or
                    ![string]::Equals($Owned.MainModule.FileName, $ExpectedWorkerPath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Worker identity changed between discovery and opening its retained handle'
                }
                $null = Add-ObservedProcess $Owned 'worker' $ExpectedWorkerPath $false
                $Owned = $null # Ownership transferred only after PID + creation time + image validation.
            } catch { $Evidence.workerDiscoveryErrors++; $DiscoveryComplete = $false }
            finally { if ($Owned) { $Owned.Dispose() } }
        }
        $SampleElapsed = $Timer.Elapsed.TotalSeconds
        $Interval = if ($null -ne $PreviousElapsed) { $SampleElapsed - $PreviousElapsed } else { $null }
        $Phase = if ($SampleElapsed -lt $WarmupSeconds) { 'startup-warmup' } else { 'measurement' }
        if ($Phase -eq 'measurement' -and !$WarmStorage) { $WarmStorage = Get-StorageSizes }
        $CpuDelta = 0.0; $CpuComplete = ($null -ne $Interval) -and $DiscoveryComplete; $Private = 0L; $Working = 0L; $Handles = 0; $Threads = 0; $Active = 0
        foreach ($Child in $Children) {
            if ($Child.SeenExited) { continue }
            $Process = $Child.Process
            try {
                $Process.Refresh()
                if ($Process.HasExited) {
                    $Child.SeenExited = $true; $Child.Record.naturalExitCode = $Process.ExitCode
                    $Child.Record.finalCpuSeconds = $Process.TotalProcessorTime.TotalSeconds
                    $CpuComplete = $false; continue
                }
                $Cpu = $Process.TotalProcessorTime.TotalSeconds
                $Delta = if ($null -ne $Child.LastCpu) { [Math]::Max(0.0, [double]($Cpu - $Child.LastCpu)) } else { $null }
                if ($null -eq $Delta) { $CpuComplete = $false } else { $CpuDelta += $Delta }
                $Child.LastCpu = $Cpu; $Child.Record.lastObservedCpuSeconds = $Cpu
                $ProcessPrivate = $Process.PrivateMemorySize64; $ProcessWorking = $Process.WorkingSet64
                $ProcessHandles = $Process.HandleCount; $ProcessThreads = $Process.Threads.Count
                $Private += $ProcessPrivate; $Working += $ProcessWorking; $Handles += $ProcessHandles; $Threads += $ProcessThreads; $Active++
                $Rows.Add([pscustomobject]@{ elapsedSeconds = $SampleElapsed; utc = [DateTimeOffset]::UtcNow.ToString('o'); phase = $Phase;
                    kind = $Child.Record.kind; pid = $Process.Id; startedUtc = $Child.Record.startedUtc; totalCpuSeconds = $Cpu; deltaCpuSeconds = $Delta;
                    privateBytes = $ProcessPrivate; workingSetBytes = $ProcessWorking; handleCount = $ProcessHandles; threadCount = $ProcessThreads })
            } catch { $CpuComplete = $false; $Child.LastCpu = $null; $Child.Record.sampleError = $_.Exception.GetType().Name }
        }
        $Visible = $false; $Minimized = $false; $Dpi = $null
        if ($Desktop) {
            $Window = [PerfMonitorBaseline.Native]::Find([uint32]$Desktop.Id)
            $Visible = $Window -ne [IntPtr]::Zero
            if ($Visible) { $Minimized = [PerfMonitorBaseline.Native]::IsIconic($Window); $Dpi = [PerfMonitorBaseline.Native]::GetDpiForWindow($Window) }
            if ($Phase -eq 'measurement' -and (($Mode -eq 'desktop-visible' -and (!$Visible -or $Minimized)) -or ($Mode -eq 'desktop-hidden' -and $Visible))) { $VisibilityMismatch++ }
        }
        $Storage = Get-StorageSizes
        $SetRows.Add([pscustomobject]@{ elapsedSeconds = $SampleElapsed; utc = [DateTimeOffset]::UtcNow.ToString('o'); phase = $Phase; intervalSeconds = $Interval; intervalStartSeconds = $PreviousElapsed;
            cpuCoverageComplete = $CpuComplete; workerDiscoveryComplete = $DiscoveryComplete; deltaCpuSeconds = $CpuDelta;
            cpuCoreEquivalentPct = if ($CpuComplete -and $Interval -gt 0) { 100 * $CpuDelta / $Interval } else { $null };
            cpuMachineNormalizedPct = if ($CpuComplete -and $Interval -gt 0) { 100 * $CpuDelta / $Interval / $Computer.NumberOfLogicalProcessors } else { $null };
            privateBytes = $Private; workingSetBytes = $Working; handleCount = $Handles; threadCount = $Threads; activeProcesses = $Active;
            dbBytes = $Storage.dbBytes; walBytes = $Storage.walBytes; shmBytes = $Storage.shmBytes;
            dbDeltaBytes = if ($PreviousStorage) { $Storage.dbBytes - $PreviousStorage.dbBytes } else { $null };
            walDeltaBytes = if ($PreviousStorage) { $Storage.walBytes - $PreviousStorage.walBytes } else { $null };
            desktopVisible = $Visible; desktopMinimized = $Minimized; desktopDpi = $Dpi;
            probeLatenessSeconds = [Math]::Max(0.0, [double]($SampleElapsed - $NextProbe)); discoverySeconds = $SampleElapsed - $DiscoverStarted })
        $PreviousElapsed = $SampleElapsed
        $PreviousStorage = $Storage
        $NextProbe += $ProbeIntervalMilliseconds / 1000.0
        while ($NextProbe -le $Timer.Elapsed.TotalSeconds) { $NextProbe += $ProbeIntervalMilliseconds / 1000.0; $Evidence.skippedProbeSlots++ }
    }
    $Evidence.measuredElapsedSeconds = $Timer.Elapsed.TotalSeconds
    $Evidence.windowStateMismatchSamples = $VisibilityMismatch
    $Evidence.storageBeforeShutdown = Get-StorageSizes
    $Evidence.storageAtFirstMeasurementProbe = $WarmStorage
    if ($VisibilityMismatch) { throw 'Observed Desktop state did not match the requested measurement mode' }
    # The finite-duration Agent drains normally; Desktop has no CLI exit command.
    if ($Desktop -and !$Desktop.HasExited) { $Desktop.Kill(); $null = $Desktop.WaitForExit(5000) }
    if (!$Agent.WaitForExit(15000)) { throw 'Finite-duration Agent did not finish within the cleanup allowance' }
    if ($Agent.ExitCode -ne 0) { throw "Agent exited with code $($Agent.ExitCode)" }
    $Evidence.completed = $true
} catch {
    $Evidence.errorType = $_.Exception.GetType().Name
    $Evidence.error = $_.Exception.Message
    throw
} finally {
    $Timer.Stop()
    foreach ($Child in $Children) {
        try {
            $Child.Process.Refresh()
            if (!$Child.Process.HasExited) { $Child.Process.Kill(); if (!$Child.Process.WaitForExit(5000)) { throw 'Owned child cleanup timeout' }; $Child.Record.cleanup = 'terminated owned process' }
            else { $Child.Record.cleanup = 'already exited' }
            $Child.Record.exitCode = $Child.Process.ExitCode
            $Child.Record.finalCpuSeconds = $Child.Process.TotalProcessorTime.TotalSeconds
            if ($Child.Copies.Count) {
                $AllCopies = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$Child.Copies)
                if (!$AllCopies.Wait(5000)) { throw 'Owned child stream-drain timeout' }
                $AllCopies.GetAwaiter().GetResult()
            }
        } catch { $Child.Record.cleanupError = $_.Exception.GetType().Name; $Evidence.completed = $false }
        finally { foreach ($Stream in $Child.Streams) { $Stream.Dispose() }; $Child.Process.Dispose() }
    }
    $Evidence.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if (!$Evidence.storageBeforeShutdown) { $Evidence.storageBeforeShutdown = Get-StorageSizes }
    $Evidence.storageAfterShutdown = Get-StorageSizes
    $Evidence.observedWorkerInstances = @($Processes | Where-Object { $_.kind -eq 'worker' }).Count
    $Rows | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'process-resources.csv') -NoTypeInformation -Encoding utf8
    $SetRows | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'product-resources.csv') -NoTypeInformation -Encoding utf8
    $CompleteRows = @($SetRows | Where-Object { $_.phase -eq 'measurement' -and $_.intervalStartSeconds -ge $WarmupSeconds -and $_.cpuCoverageComplete -and $_.intervalSeconds -gt 0 })
    $SteadyRows = @($SetRows | Where-Object { $_.phase -eq 'measurement' })
    $CpuSeconds = [double]($CompleteRows | Measure-Object deltaCpuSeconds -Sum).Sum
    $CoveredSeconds = [double]($CompleteRows | Measure-Object intervalSeconds -Sum).Sum
    $Evidence.summary = [ordered]@{ probeSamples = $SetRows.Count; measurementSamples = $SteadyRows.Count; cpuCompleteSamples = $CompleteRows.Count;
        cpuCoveredSeconds = $CoveredSeconds; observedCpuDeltaSeconds = $CpuSeconds; cpuCoverageFractionOfRequestedMeasurement = $CoveredSeconds / $DurationSeconds;
        cpuCoreEquivalentMeanPct = if ($CoveredSeconds -gt 0) { 100 * $CpuSeconds / $CoveredSeconds } else { $null };
        cpuMachineNormalizedMeanPct = if ($CoveredSeconds -gt 0) { 100 * $CpuSeconds / $CoveredSeconds / $Computer.NumberOfLogicalProcessors } else { $null };
        peakPrivateBytes = ($SteadyRows | Measure-Object privateBytes -Maximum).Maximum;
        peakWorkingSetBytes = ($SteadyRows | Measure-Object workingSetBytes -Maximum).Maximum;
        peakHandles = ($SteadyRows | Measure-Object handleCount -Maximum).Maximum; peakThreads = ($SteadyRows | Measure-Object threadCount -Maximum).Maximum;
        peakDbBytes = ($SetRows | Measure-Object dbBytes -Maximum).Maximum; peakWalBytes = ($SetRows | Measure-Object walBytes -Maximum).Maximum;
        databaseGrowthBeforeShutdownBytes = if ($WarmStorage) { $Evidence.storageBeforeShutdown.dbBytes - $WarmStorage.dbBytes } else { $null };
        walGrowthBeforeShutdownBytes = if ($WarmStorage) { $Evidence.storageBeforeShutdown.walBytes - $WarmStorage.walBytes } else { $null };
        maximumProbeIntervalSeconds = ($SetRows | Measure-Object intervalSeconds -Maximum).Maximum;
        probeGapSamples = @($SetRows | Where-Object { $_.intervalSeconds -gt 1.5 * $ProbeIntervalMilliseconds / 1000.0 }).Count }
    if ($Evidence.completed) {
        try {
            $History = & $PythonPath (Join-Path $PSScriptRoot 'read_baseline_history.py') (Join-Path $Data 'history-v1.db')
            if ($LASTEXITCODE -ne 0) { throw 'Read-only history inspection failed' }
            $Evidence.history = ($History -join "`n") | ConvertFrom-Json
            if ($Evidence.history.integrityCheck -ne 'ok') { throw 'Baseline database integrity did not pass' }
        } catch { $Evidence.completed = $false; $Evidence.historyInspectionError = $_.Exception.Message }
    }
    $Evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'baseline.json') -Encoding utf8
}
if (!$Evidence.completed) { throw "Baseline incomplete; see $OutputDirectory/baseline.json" }
Write-Output (Join-Path $OutputDirectory 'baseline.json')
