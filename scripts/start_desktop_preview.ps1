<#
Commit-bound development preview, with isolated data and owned-process cleanup.
By default this only checks artifacts. Starting requires -StartHidden or
-StartVisible; hidden previews also require a positive -DurationSeconds.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactsPath,
    [string]$RuntimeRoot = '',
    [switch]$CheckOnly,
    [switch]$StartHidden,
    [switch]$StartVisible,
    [ValidateRange(0, 86400)][int]$DurationSeconds = 0
)
$ErrorActionPreference = 'Stop'
if ($StartHidden -and $StartVisible) { throw 'preview_conflicting_start_modes' }
$check = $CheckOnly -or (!$StartHidden -and !$StartVisible)
if (!$check -and $StartHidden -and $DurationSeconds -eq 0) { throw 'preview_hidden_duration_required' }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = if ([IO.Path]::IsPathRooted($ArtifactsPath)) { $ArtifactsPath } else { Join-Path $repoRoot $ArtifactsPath }
$artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
$expectedHead = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'preview_git_head_unavailable' }
$buildProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$productVersion = $buildProps.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$expectedVersion = $productVersion + '+' + $expectedHead
if (!$RuntimeRoot) {
    $sdkVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $RuntimeRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('PerfMonitor\dotnet-sdk-' + $sdkVersion)
}
$RuntimeRoot = (Resolve-Path -LiteralPath $RuntimeRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $RuntimeRoot 'dotnet.exe') -PathType Leaf)) { throw 'preview_runtime_missing' }
$agentRoot = Join-Path $artifactRoot 'bin\PerfMonitor.Agent\release_win-x64'
$desktopRoot = Join-Path $artifactRoot 'bin\PerfMonitor.Desktop\release_win-x64'
$agentPath = Join-Path $agentRoot 'perf-monitor-agent.exe'
$desktopPath = Join-Path $desktopRoot 'perf-monitor-desktop.exe'
$artifacts = @($agentPath, [IO.Path]::ChangeExtension($agentPath, '.dll'),
    $desktopPath, [IO.Path]::ChangeExtension($desktopPath, '.dll')) | ForEach-Object {
    if (!(Test-Path -LiteralPath $_ -PathType Leaf)) { throw "preview_product_missing: $_" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($_)
    if ($info.ProductVersion -cne $expectedVersion -or $info.FileVersion -cne ($productVersion + '.0')) {
        throw "preview_product_binding_mismatch: $_; expected $expectedVersion, found $($info.ProductVersion)"
    }
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash;
        fileVersion = $info.FileVersion; productVersion = $info.ProductVersion }
}
$fingerprints = foreach ($directory in @($agentRoot, $desktopRoot)) {
    $lines = @(Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object {
        $_.FullName.Substring($directory.Length + 1).Replace('\', '/') + '|' +
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    [ordered]@{ directory = $directory; fileCount = $lines.Count; sha256 = $hash }
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $pipePath = '\\.\pipe\PerfMonitor.' + $identity.User.Value + '.agent' }
finally { $identity.Dispose() }
$existing = @(Get-CimInstance Win32_Process -Filter "Name='perf-monitor-agent.exe' OR Name='perf-monitor-desktop.exe'")
$pipePresent = [IO.Directory]::GetFiles('\\.\pipe\') -contains $pipePath
$status = @(& git -C $repoRoot status --porcelain)
$evidence = [ordered]@{ purpose = 'commit-bound local development preview; not an installer';
    repositoryHead = $expectedHead; workingTreeDirty = ($status.Count -gt 0);
    bindingLimit = 'Commit version metadata does not certify uncommitted source; build and review the named artifacts separately.';
    mode = $(if ($check) { 'check-only' } elseif ($StartHidden) { 'hidden' } else { 'visible' });
    artifacts = $artifacts; directoryFingerprints = $fingerprints;
    fingerprintAlgorithm = 'SHA-256 over UTF-8 ordinal-sorted relative-path|UPPERCASE-file-SHA256 lines, LF terminated';
    runtimeRoot = $RuntimeRoot; existingProductProcessCount = $existing.Count; pipePresent = $pipePresent;
    canLaunch = ($existing.Count -eq 0 -and !$pipePresent) }
if ($check) { $evidence | ConvertTo-Json -Depth 7; return }
if (!$evidence.canLaunch) { throw 'preview_existing_process_or_pipe' }
$runRoot = Join-Path $artifactRoot ('preview-runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$dataRoot = Join-Path $runRoot 'isolated-data'
$null = New-Item -ItemType Directory -Path $dataRoot
$children = [Collections.Generic.List[object]]::new()
$evidence.startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
$evidence.dataDirectory = $dataRoot
$evidence.processes = [Collections.Generic.List[object]]::new()
function Start-OwnedChild([string]$kind, [string]$path, [string]$arguments, [bool]$visible) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $path; $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = Split-Path -Path $path
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = !$visible
    $startInfo.WindowStyle = if ($visible) { [Diagnostics.ProcessWindowStyle]::Normal } else { [Diagnostics.ProcessWindowStyle]::Hidden }
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $RuntimeRoot
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $RuntimeRoot
    $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $startInfo
    if (!$process.Start()) { $process.Dispose(); throw "preview_child_start_failed: $kind" }
    $null = $process.Handle
    $record = [ordered]@{ kind = $kind; path = $path; arguments = $arguments; pid = $process.Id;
        startedUtc = $process.StartTime.ToUniversalTime().ToString('o'); cleanup = 'pending' }
    $child = [pscustomobject]@{ Process = $process; Record = $record; Streams = @(); Copies = @() }
    $children.Add($child); $evidence.processes.Add($record)
    foreach ($channel in @('stdout', 'stderr')) {
        $stream = [IO.File]::Create((Join-Path $runRoot ($kind + '.' + $channel + '.log')))
        $child.Streams += $stream
        $inputStream = if ($channel -eq 'stdout') { $process.StandardOutput.BaseStream } else { $process.StandardError.BaseStream }
        $child.Copies += $inputStream.CopyToAsync($stream)
    }
    return $process
}
$timer = $null
$cleanupFailed = $false
try {
    $agent = Start-OwnedChild 'agent' $agentPath ('--quiet --warmup-seconds 0 --data-directory "' + $dataRoot + '"') $false
    $desktopArguments = if ($StartHidden) { '--start-hidden' } else { '' }
    $desktop = Start-OwnedChild 'desktop' $desktopPath $desktopArguments ([bool]$StartVisible)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Preview $($evidence.mode). Evidence: $runRoot"
    while (!$desktop.HasExited) {
        if ($agent.HasExited) { throw 'preview_agent_exited; see agent.stderr.log' }
        if ($DurationSeconds -gt 0 -and $timer.Elapsed.TotalSeconds -ge $DurationSeconds) { break }
        Start-Sleep -Milliseconds 100
    }
    $evidence.agentAliveBeforeWrapperCleanup = !$agent.HasExited
    $evidence.desktopExitedNaturally = $desktop.HasExited
    $evidence.actualDurationSeconds = $timer.Elapsed.TotalSeconds
    if ($desktop.HasExited) {
        $evidence.desktopExitCode = $desktop.ExitCode
        if ($desktop.ExitCode -ne 0) { throw "preview_desktop_failed: $($desktop.ExitCode); see desktop.stderr.log" }
        if ($StartHidden -and $timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
            throw 'preview_hidden_residency_ended_early'
        }
    }
    $evidence.completed = $true
} catch { $evidence.completed = $false; $evidence.error = $_.Exception.Message; throw }
finally {
    foreach ($child in $children) {
        try {
            $process = $child.Process
            if (!$process.HasExited) {
                $process.Kill()
                $child.Record.cleanup = 'terminated retained owned Process object'
                if (!$process.WaitForExit(5000)) { throw 'preview_owned_cleanup_timeout' }
            } else { $child.Record.cleanup = 'already exited' }
            $child.Record.exitCode = $process.ExitCode
            $child.Record.exitedUtc = $process.ExitTime.ToUniversalTime().ToString('o')
            foreach ($copy in $child.Copies) {
                if (!$copy.Wait(5000)) { throw 'preview_owned_log_drain_timeout' }
                $copy.GetAwaiter().GetResult()
            }
        } catch {
            $cleanupFailed = $true
            $child.Record.cleanupError = $_.Exception.Message
        }
        finally { foreach ($stream in $child.Streams) { $stream.Dispose() }; $child.Process.Dispose() }
    }
    if ($timer) { $evidence.actualDurationSeconds = $timer.Elapsed.TotalSeconds }
    $evidence.cleanupFailed = $cleanupFailed
    if ($cleanupFailed) { $evidence.completed = $false }
    $evidence.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $runRoot 'preview-evidence.json') -Encoding utf8
    if ($cleanupFailed) { throw 'preview_owned_cleanup_failed; see preview-evidence.json' }
}
