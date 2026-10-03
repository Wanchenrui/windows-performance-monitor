#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework

function Show-LaunchMessage([string]$Message, [bool]$IsError = $true) {
    $icon = if ($IsError) { [System.Windows.MessageBoxImage]::Error } else { [System.Windows.MessageBoxImage]::Information }
    $null = [System.Windows.MessageBox]::Show($Message, 'PerfMonitor', [System.Windows.MessageBoxButton]::OK, $icon)
}

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-SamePath([string]$Left, [string]$Right) {
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return [string]::Equals((Get-FullPath $Left), (Get-FullPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

$launchMutex = $null
$launchLockAcquired = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $mutexName = 'Local\PerfMonitor.LocalCandidate.Launch.' + $identity.User.Value }
    finally { $identity.Dispose() }
    $launchMutex = New-Object System.Threading.Mutex($false, $mutexName)
    try { $launchLockAcquired = $launchMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $launchLockAcquired = $true }
    if (!$launchLockAcquired) { return }
    $localRoot = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($localRoot)) { throw '无法取得当前用户的本地应用目录。' }
    $installRoot = Get-FullPath (Join-Path $localRoot 'Programs\PerfMonitor')
    if (!(Test-SamePath $PSScriptRoot $installRoot)) { throw "请从当前用户安装的快捷方式启动 PerfMonitor。`n安装目录：$installRoot" }
    $marker = Get-Content -LiteralPath (Join-Path $installRoot 'local-install.json') -Raw | ConvertFrom-Json
    if ($marker.Product -cne 'PerfMonitor.LocalCandidate' -or $marker.SchemaVersion -ne 1 -or
        ![IO.Path]::IsPathRooted([string]$marker.InstallRoot) -or !(Test-SamePath ([string]$marker.InstallRoot) $installRoot)) {
        throw '安装标记不正确，请重新进行当前用户安装。'
    }

    $agentPath = Join-Path $installRoot 'agent\perf-monitor-agent.exe'
    $desktopPath = Join-Path $installRoot 'desktop\perf-monitor-desktop.exe'
    $workerPath = Join-Path $installRoot 'agent\provider-worker\perf-monitor-provider-worker.exe'
    $runtimeRoot = Join-Path $installRoot 'runtime'
    foreach ($requiredFile in @($agentPath, $desktopPath, $workerPath, (Join-Path $runtimeRoot 'dotnet.exe'))) {
        if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "安装文件缺失：$requiredFile" }
    }
    foreach ($requiredDirectory in @('host', 'shared\Microsoft.NETCore.App', 'shared\Microsoft.WindowsDesktop.App')) {
        if (!(Test-Path -LiteralPath (Join-Path $runtimeRoot $requiredDirectory) -PathType Container)) {
            throw "安装的 .NET Desktop Runtime 不完整：$requiredDirectory"
        }
    }

    $sessionId = (Get-Process -Id $PID).SessionId
    $instances = @(Get-CimInstance Win32_Process -Filter "SessionId=$sessionId AND (Name='perf-monitor-agent.exe' OR Name='perf-monitor-desktop.exe')")
    foreach ($instance in $instances) {
        $expectedPath = if ($instance.Name -ieq 'perf-monitor-agent.exe') { $agentPath } else { $desktopPath }
        if (!(Test-SamePath ([string]$instance.ExecutablePath) $expectedPath)) {
            $location = if ($instance.ExecutablePath) { $instance.ExecutablePath } else { '无法读取进程路径' }
            throw "检测到其他位置运行的 PerfMonitor（PID $($instance.ProcessId)）。`n$location`n`n请先从该实例的托盘菜单退出，再启动当前安装。"
        }
    }
    $existingDesktop = @($instances | Where-Object { $_.Name -ieq 'perf-monitor-desktop.exe' })
    if ($existingDesktop.Count -gt 0) {
        Show-LaunchMessage 'PerfMonitor 已在运行。请点击通知区域的 PerfMonitor 托盘图标恢复窗口；图标也可能位于隐藏图标列表中。' $false
        return
    }

    # These settings apply only to this launcher and its children.
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $runtimeRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $runtimeRoot, 'Process')
    $logsRoot = Join-Path $localRoot 'PerfMonitor\logs'
    $null = [IO.Directory]::CreateDirectory($logsRoot)
    $existingAgent = @($instances | Where-Object { $_.Name -ieq 'perf-monitor-agent.exe' })
    $agent = $null
    $startedAgent = $false
    if ($existingAgent.Count -gt 0) {
        $agent = Get-Process -Id ([int]$existingAgent[0].ProcessId) -ErrorAction Stop
        if (!(Test-SamePath $agent.Path $agentPath)) { throw 'Agent 状态在启动期间发生变化，请再次启动。' }
    } else {
        $runId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N')
        $stdoutLog = Join-Path $logsRoot ('agent-' + $runId + '.stdout.log')
        $stderrLog = Join-Path $logsRoot ('agent-' + $runId + '.stderr.log')
        $agent = Start-Process -FilePath $agentPath -ArgumentList '--quiet' -WorkingDirectory (Split-Path -Parent $agentPath) -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
        $startedAgent = $true
        # Observe real startup failure only; no synthetic load or IPC probes.
        if ($agent.WaitForExit(1500)) {
            throw "Agent 启动后退出，退出码：$($agent.ExitCode)。`n`n错误日志：$stderrLog`n输出日志：$stdoutLog"
        }
    }

    # Check again immediately before opening the UI to avoid a second window.
    $latestDesktop = @(Get-CimInstance Win32_Process -Filter "SessionId=$sessionId AND Name='perf-monitor-desktop.exe'")
    if ($latestDesktop.Count -gt 0) {
        foreach ($instance in $latestDesktop) {
            if (!(Test-SamePath ([string]$instance.ExecutablePath) $desktopPath)) {
                throw '启动期间检测到另一位置的 Desktop，请先从其托盘菜单退出。'
            }
        }
        Show-LaunchMessage 'PerfMonitor 已在运行。请点击通知区域的 PerfMonitor 托盘图标恢复窗口。' $false
        return
    }
    $desktop = Start-Process -FilePath $desktopPath -WorkingDirectory (Split-Path -Parent $desktopPath) -WindowStyle Normal -PassThru
    if ($desktop.WaitForExit(1500) -and $desktop.ExitCode -ne 0) {
        throw "Desktop 启动后退出，退出码：$($desktop.ExitCode)。`n`n请查看日志目录：$logsRoot"
    }
    $agent.Refresh()
    if ($agent.HasExited) {
        $logHint = if ($startedAgent) { "错误日志：$stderrLog`n输出日志：$stdoutLog" } else { "请查看日志目录：$logsRoot" }
        throw "Agent 已退出，退出码：$($agent.ExitCode)。`n`n$logHint"
    }
} catch {
    Show-LaunchMessage ("无法启动 PerfMonitor。`n`n" + $_.Exception.Message)
    exit 1
} finally {
    if ($launchLockAcquired) { $launchMutex.ReleaseMutex() }
    if ($null -ne $launchMutex) { $launchMutex.Dispose() }
}
