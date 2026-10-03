#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PerfMonitorInstallerWindowPosition {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

$script:version = '1.1.0'
$script:installRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PerfMonitor'))
$script:dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PerfMonitor\data'
$script:payloadRoot = Join-Path $PSScriptRoot 'payload'
$script:registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PerfMonitorLocalCandidate'
$script:desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PerfMonitor.lnk'
$script:menuRoot = Join-Path ([Environment]::GetFolderPath('Programs')) 'PerfMonitor'
$script:menuLink = Join-Path $script:menuRoot 'PerfMonitor.lnk'
$script:busy = $false
$script:installed = $false
$script:exitCode = 0

function Update-InstallStatus([string] $Text, [double] $Progress) {
    $status.Text = $Text
    $progressBar.Value = $Progress
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
}

function Assert-ReadyToInstall {
    if (Test-Path -LiteralPath $script:installRoot) {
        $item = Get-Item -LiteralPath $script:installRoot -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw '安装位置已被其他文件或链接占用，请先处理该位置。'
        }
        if (@(Get-ChildItem -LiteralPath $script:installRoot -Force).Count -gt 0) {
            throw '安装位置已有文件或已安装版本。请先通过原有卸载入口卸载，再安装此候选版。'
        }
    }
    if (Test-Path -LiteralPath $script:registryPath) {
        throw '检测到已安装的开发候选版，请先在 Windows「设置 → 应用」中卸载 PerfMonitor。'
    }
    if ((Test-Path -LiteralPath $script:desktopLink) -or (Test-Path -LiteralPath $script:menuLink)) {
        throw '检测到已有 PerfMonitor 快捷方式，请先卸载原有版本或移除旧快捷方式后再安装。'
    }
    $required = @(
        'agent\perf-monitor-agent.exe', 'agent\provider-worker\perf-monitor-provider-worker.exe',
        'desktop\perf-monitor-desktop.exe', 'runtime\dotnet.exe', 'runtime\host',
        'runtime\shared\Microsoft.NETCore.App\10.0.10',
        'runtime\shared\Microsoft.WindowsDesktop.App\10.0.10',
        'runtime\LICENSE.txt', 'runtime\ThirdPartyNotices.txt',
        'Launch-PerfMonitor.ps1', 'Launch-PerfMonitor.vbs',
        'Uninstall-PerfMonitor.ps1', 'Uninstall-PerfMonitor.cmd'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:payloadRoot $relative))) {
            throw ('安装包不完整，缺少：' + $relative + '。请重新解压完整安装包。')
        }
    }
    $sessionId = (Get-Process -Id $PID).SessionId
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "SessionId = $sessionId")
    foreach ($process in $processes) {
        $productProcess = $process.Name -match '^perf-monitor-(agent|desktop|provider-worker)\.exe$'
        $hostedProduct = $process.Name -ieq 'dotnet.exe' -and $process.CommandLine -match 'perf-monitor-(agent|desktop|provider-worker)\.dll'
        if ($productProcess -or $hostedProduct) {
            throw '检测到当前会话中的 PerfMonitor 仍在运行。请先退出桌面端并停止 Agent，再安装。安装程序不会强制结束进程。'
        }
    }
}

function New-PerfMonitorShortcut([string] $Path) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
        $shortcut.Arguments = '"' + (Join-Path $script:installRoot 'Launch-PerfMonitor.vbs') + '"'
        $shortcut.WorkingDirectory = $script:installRoot
        $shortcut.IconLocation = (Join-Path $script:installRoot 'desktop\perf-monitor-desktop.exe') + ',0'
        $shortcut.Description = 'PerfMonitor 1.1.0 开发候选版'
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

function Install-PerfMonitor {
    $writtenFiles = New-Object 'System.Collections.Generic.List[string]'
    $createdDirectories = New-Object 'System.Collections.Generic.List[string]'
    $createdLinks = New-Object 'System.Collections.Generic.List[string]'
    $registryCreated = $false
    try {
        Assert-ReadyToInstall
        $payloadItems = @(Get-ChildItem -LiteralPath $script:payloadRoot -Recurse -Force)
        foreach ($item in $payloadItems) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装包包含文件链接，无法安全安装。请重新解压原始安装包。' }
        }
        if (-not (Test-Path -LiteralPath $script:installRoot)) {
            [void](New-Item -ItemType Directory -Path $script:installRoot)
            $createdDirectories.Add($script:installRoot)
        }
        $directories = @($payloadItems | Where-Object { $_.PSIsContainer } | Sort-Object { $_.FullName.Length })
        foreach ($directory in $directories) {
            $relative = $directory.FullName.Substring($script:payloadRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
            $destination = Join-Path $script:installRoot $relative
            [void](New-Item -ItemType Directory -Path $destination)
            $createdDirectories.Add($destination)
        }
        $files = @($payloadItems | Where-Object { -not $_.PSIsContainer })
        $index = 0
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($script:payloadRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
            $destination = Join-Path $script:installRoot $relative
            $writtenFiles.Add($destination)
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $index++
            Update-InstallStatus ('正在安装文件（' + $index + ' / ' + $files.Count + '）…') (85 * $index / [Math]::Max(1, $files.Count))
        }
        $markerPath = Join-Path $script:installRoot 'local-install.json'
        $writtenFiles.Add($markerPath)
        [ordered]@{
            SchemaVersion = 1
            InstallRoot = $script:installRoot
            Product = 'PerfMonitor.LocalCandidate'
            Version = $script:version
            InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
        Update-InstallStatus '正在创建快捷方式…' 90
        if (-not (Test-Path -LiteralPath $script:menuRoot)) {
            [void](New-Item -ItemType Directory -Path $script:menuRoot)
            $createdDirectories.Add($script:menuRoot)
        }
        foreach ($link in @($script:desktopLink, $script:menuLink)) {
            $createdLinks.Add($link)
            New-PerfMonitorShortcut $link
        }
        [void](New-Item -Path $script:registryPath)
        $registryCreated = $true
        $values = @{
            DisplayName = 'PerfMonitor（开发候选）'
            DisplayVersion = $script:version
            InstallLocation = $script:installRoot
            DisplayIcon = (Join-Path $script:installRoot 'desktop\perf-monitor-desktop.exe') + ',0'
            UninstallString = '"' + (Join-Path $env:SystemRoot 'System32\cmd.exe') + '" /d /c ""' + (Join-Path $script:installRoot 'Uninstall-PerfMonitor.cmd') + '""'
        }
        foreach ($name in $values.Keys) {
            [void](New-ItemProperty -Path $script:registryPath -Name $name -Value $values[$name] -PropertyType String)
        }
        foreach ($name in @('NoModify', 'NoRepair')) {
            [void](New-ItemProperty -Path $script:registryPath -Name $name -Value 1 -PropertyType DWord)
        }
        $script:installed = $true
        $script:exitCode = 0
        $heading.Text = '安装完成'
        $description.Text = 'PerfMonitor 已安装到当前用户。点击下方按钮开始体验；以后也可从桌面或开始菜单打开。'
        Update-InstallStatus '安装成功。首次打开后，采样记录会写入下方的数据目录。' 100
        $installButton.Content = '打开 PerfMonitor'
        $cancelButton.Content = '完成'
    }
    catch {
        $failure = $_.Exception.Message
        $cleanupFailed = $false
        if ($registryCreated) {
            try { Remove-Item -LiteralPath $script:registryPath -Recurse -ErrorAction Stop } catch { $cleanupFailed = $true }
        }
        foreach ($path in $createdLinks) {
            try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop } } catch { $cleanupFailed = $true }
        }
        foreach ($path in $writtenFiles) {
            try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop } } catch { $cleanupFailed = $true }
        }
        foreach ($path in @($createdDirectories | Sort-Object Length -Descending)) {
            try {
                if ((Test-Path -LiteralPath $path) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) {
                    Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                }
            } catch { $cleanupFailed = $true }
        }
        $script:exitCode = 1
        $heading.Text = '暂时无法安装'
        $status.Text = $failure
        if ($cleanupFailed) { $status.Text += "`n部分新文件无法清理，请关闭占用文件的程序后检查安装目录。" }
        $progressBar.Value = 0
        $installButton.Content = '重试安装'
    }
}

[xml] $xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="安装 PerfMonitor" Width="640" Height="660" MinWidth="640" MinHeight="660" WindowStartupLocation="CenterScreen" ResizeMode="CanMinimize" Background="#F6F8FC" FontFamily="Microsoft YaHei UI" FontSize="14">
  <Grid Margin="32">
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <StackPanel>
      <TextBlock Text="PerfMonitor" Foreground="#214A8C" FontSize="16" FontWeight="SemiBold"/>
      <TextBlock x:Name="Heading" Text="安装电脑性能监控" FontSize="26" FontWeight="SemiBold" Margin="0,10,0,12"/>
      <TextBlock x:Name="Description" Text="1.1.0 开发候选版 · 当前用户安装。运行后直接采集本机数据。" TextWrapping="Wrap" Foreground="#46556C"/>
    </StackPanel>
    <StackPanel Grid.Row="1" Margin="0,24,0,20">
      <TextBlock Text="安装位置" FontWeight="SemiBold"/>
      <TextBox x:Name="InstallPath" IsReadOnly="True" Background="White" BorderBrush="#D8DEEA" Padding="10" Margin="0,8,0,14"/>
      <TextBlock Text="采样数据位置" FontWeight="SemiBold"/>
      <TextBox x:Name="DataPath" IsReadOnly="True" Background="White" BorderBrush="#D8DEEA" Padding="10" Margin="0,8,0,14"/>
      <TextBlock Text="运行时随安装包提供；创建桌面和开始菜单快捷方式，无需管理员权限。打开程序后开始采样，可通过 Windows 设置或安装目录中的卸载入口卸载。" TextWrapping="Wrap" Foreground="#46556C" Margin="0,0,0,16"/>
      <ProgressBar x:Name="Progress" Height="6" Minimum="0" Maximum="100" Foreground="#3065C6" Background="#E1E7F2"/>
      <TextBlock x:Name="Status" Text="准备就绪，点击「安装」继续。" TextWrapping="Wrap" Margin="0,12,0,0" Foreground="#46556C"/>
    </StackPanel>
    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button x:Name="Cancel" Content="取消" MinWidth="90" Padding="15,9" Margin="0,0,12,0"/>
      <Button x:Name="Install" Content="安装" MinWidth="140" Padding="15,9" Background="#3065C6" Foreground="White"/>
    </StackPanel>
  </Grid>
</Window>
'@
$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$heading = $window.FindName('Heading')
$description = $window.FindName('Description')
$status = $window.FindName('Status')
$progressBar = $window.FindName('Progress')
$installButton = $window.FindName('Install')
$cancelButton = $window.FindName('Cancel')
$window.FindName('InstallPath').Text = $script:installRoot
$window.FindName('DataPath').Text = $script:dataRoot
$window.Add_Loaded({
    # Native coordinates keep placement on the secondary display correct across DPI settings.
    # This moves only this installer's own window and does not send user input.
    try {
        $secondary = @([Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary })
        if ($secondary.Count -gt 0) {
            $area = $secondary[0].WorkingArea
            $handle = (New-Object Windows.Interop.WindowInteropHelper $window).Handle
            $rectangle = New-Object PerfMonitorInstallerWindowPosition+Rect
            if ([PerfMonitorInstallerWindowPosition]::GetWindowRect($handle, [ref] $rectangle)) {
                $left = $area.Left + [Math]::Max(0, [int](($area.Width - ($rectangle.Right - $rectangle.Left)) / 2))
                $top = $area.Top + [Math]::Max(0, [int](($area.Height - ($rectangle.Bottom - $rectangle.Top)) / 2))
                [void][PerfMonitorInstallerWindowPosition]::SetWindowPos($handle, [IntPtr]::Zero, $left, $top, 0, 0, 0x0015)
            }
        }
    } catch { } # Standard centered placement remains usable if display lookup fails.
})
$window.Add_Closing({ param($sender, $eventArgs) if ($script:busy) { $eventArgs.Cancel = $true } })
$cancelButton.Add_Click({ if (-not $script:busy) { $window.Close() } })
$installButton.Add_Click({
    if ($script:busy) { return }
    if ($script:installed) {
        try {
            Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\wscript.exe') -ArgumentList ('"' + (Join-Path $script:installRoot 'Launch-PerfMonitor.vbs') + '"') -WorkingDirectory $script:installRoot
            $window.Close()
        }
        catch { $status.Text = '安装已完成，但启动失败：' + $_.Exception.Message + '。可以使用桌面快捷方式重试。' }
        return
    }
    $script:busy = $true
    $installButton.IsEnabled = $false
    $cancelButton.IsEnabled = $false
    try {
        Update-InstallStatus '正在检查安装包和运行状态…' 0
        Install-PerfMonitor
    }
    finally {
        $script:busy = $false
        $installButton.IsEnabled = $true
        $cancelButton.IsEnabled = $true
    }
})
[void] $window.ShowDialog()
exit $script:exitCode
