#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework

function Get-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or ![IO.Path]::IsPathRooted($Path)) { throw '卸载路径必须为绝对路径。' }
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-SamePath([string]$Left, [string]$Right) {
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return [string]::Equals((Get-FullPath $Left), (Get-FullPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstallTarget([string]$Target, [string]$Expected) {
    if (!(Test-SamePath $Target $Expected)) { throw '拒绝卸载：目录不等于当前用户的固定安装目录。' }
    $rootItem = Get-Item -LiteralPath $Target -Force
    if (!$rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '拒绝卸载：安装根目录不是普通目录，或为链接／重解析点。'
    }
    # Walk explicitly so no linked directory is ever traversed.
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Target)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "拒绝卸载：安装目录内存在链接／重解析点。`n$($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    $markerPath = Join-Path $Target 'local-install.json'
    if (!(Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw '拒绝卸载：缺少本地安装标记。' }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.Product -cne 'PerfMonitor.LocalCandidate' -or $marker.SchemaVersion -ne 1 -or
        !(Test-SamePath ([string]$marker.InstallRoot) $Expected)) {
        throw '拒绝卸载：本地安装标记不匹配。'
    }
}

function Stop-InstalledProcesses([string[]]$OwnedPaths, [int]$SessionId) {
    # Stop the parent first so it cannot create another provider worker.
    foreach ($name in @('perf-monitor-desktop.exe', 'perf-monitor-agent.exe', 'perf-monitor-provider-worker.exe')) {
        foreach ($candidate in @(Get-CimInstance Win32_Process -Filter ("SessionId=$SessionId AND Name='" + $name + "'"))) {
            if ([string]::IsNullOrWhiteSpace([string]$candidate.ExecutablePath)) { continue }
            $candidatePath = Get-FullPath ([string]$candidate.ExecutablePath)
            if ($OwnedPaths -notcontains $candidatePath) { continue }
            $process = Get-Process -Id ([int]$candidate.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $process) { continue }
            try {
                # Re-read through the retained process object before stopping it.
                $null = $process.Handle
                $currentPath = $process.Path
                if (!(Test-SamePath $currentPath $candidatePath) -or $OwnedPaths -notcontains (Get-FullPath $currentPath)) { continue }
                Stop-Process -InputObject $process -Force -ErrorAction Stop
                if (!$process.WaitForExit(5000)) { throw "进程未能退出：$name（PID $($process.Id)）。" }
            } catch {
                $process.Refresh()
                if (!$process.HasExited) { throw }
            } finally { $process.Dispose() }
        }
    }
}

function Remove-OwnedShortcut([string]$ShortcutPath, [string]$InstallRoot, [object]$Shell) {
    if (!(Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { return }
    $shortcutItem = Get-Item -LiteralPath $ShortcutPath -Force
    if ($shortcutItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    $shortcut = $Shell.CreateShortcut($ShortcutPath)
    $owned = $false
    if (![string]::IsNullOrWhiteSpace($shortcut.TargetPath) -and [IO.Path]::IsPathRooted($shortcut.TargetPath)) {
        $target = Get-FullPath $shortcut.TargetPath
        $owned = $target.StartsWith(($InstallRoot + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)
        if (!$owned -and [IO.Path]::GetFileName($target) -ieq 'wscript.exe') {
            $argument = ([string]$shortcut.Arguments).Trim()
            $launcher = Join-Path $InstallRoot 'Launch-PerfMonitor.vbs'
            $owned = [string]::Equals($argument, ('"' + $launcher + '"'), [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($argument, $launcher, [StringComparison]::OrdinalIgnoreCase)
        }
    }
    if ($owned) { Remove-Item -LiteralPath $ShortcutPath -Force }
}

try {
    $localRoot = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($localRoot)) { throw '无法取得当前用户的本地应用目录。' }
    $expectedRoot = Get-FullPath (Join-Path $localRoot 'Programs\PerfMonitor')
    $installRoot = Get-FullPath $PSScriptRoot
    Assert-InstallTarget $installRoot $expectedRoot
    $answer = [System.Windows.MessageBox]::Show(
        "确定卸载当前用户的 PerfMonitor 本地候选版？`n`n将关闭此安装的 Desktop、Agent 和 provider worker，移除程序文件与本安装的快捷方式。`n`n所有采样数据、设置和日志都会保留：`n$(Join-Path $localRoot 'PerfMonitor')",
        '卸载 PerfMonitor', [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Question,
        [System.Windows.MessageBoxResult]::No)
    if ($answer -ne [System.Windows.MessageBoxResult]::Yes) { return }

    # Revalidate after the user confirmation and before destructive operations.
    Assert-InstallTarget $installRoot $expectedRoot
    $ownedPaths = @(
        (Get-FullPath (Join-Path $installRoot 'desktop\perf-monitor-desktop.exe')),
        (Get-FullPath (Join-Path $installRoot 'agent\perf-monitor-agent.exe')),
        (Get-FullPath (Join-Path $installRoot 'agent\provider-worker\perf-monitor-provider-worker.exe'))
    )
    $sessionId = (Get-Process -Id $PID).SessionId
    Stop-InstalledProcesses $ownedPaths $sessionId
    $remaining = @(Get-CimInstance Win32_Process -Filter "SessionId=$sessionId AND (Name='perf-monitor-agent.exe' OR Name='perf-monitor-desktop.exe' OR Name='perf-monitor-provider-worker.exe')" | Where-Object {
        $_.ExecutablePath -and $ownedPaths -contains (Get-FullPath ([string]$_.ExecutablePath))
    })
    if ($remaining.Count -gt 0) { throw '本安装仍有进程运行，请退出后重试卸载。' }
    Assert-InstallTarget $installRoot $expectedRoot
    # The running script has already been read; avoid retaining this directory as cwd.
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
    Remove-Item -LiteralPath $installRoot -Recurse -Force

    $shell = New-Object -ComObject WScript.Shell
    try {
        $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'PerfMonitor.lnk'
        $menuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'PerfMonitor\PerfMonitor.lnk'
        Remove-OwnedShortcut $desktopShortcut $installRoot $shell
        Remove-OwnedShortcut $menuShortcut $installRoot $shell
    } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }

    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PerfMonitorLocalCandidate'
    if (Test-Path -LiteralPath $uninstallKey) {
        $registration = Get-ItemProperty -LiteralPath $uninstallKey
        if (Test-SamePath ([string]$registration.InstallLocation) $installRoot) {
            Remove-Item -LiteralPath $uninstallKey -Recurse -Force
        }
    }
    $null = [System.Windows.MessageBox]::Show(
        "PerfMonitor 当前用户安装已卸载。`n`n数据、设置和日志已保留：`n$(Join-Path $localRoot 'PerfMonitor')",
        'PerfMonitor', [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
} catch {
    $null = [System.Windows.MessageBox]::Show(
        ("卸载未完成。`n`n" + $_.Exception.Message + "`n`n采样数据、设置和日志不会删除。"),
        'PerfMonitor', [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
    exit 1
}
