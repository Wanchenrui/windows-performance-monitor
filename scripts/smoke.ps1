[CmdletBinding()]
param(
    [string]$ExePath
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $ExePath) {
    $ExePath = Join-Path $ProjectRoot "dist\perf-monitor.exe"
}
if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "未找到待冒烟测试的 EXE：$ExePath"
}

$Listener = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback,
    0
)
$Listener.Start()
$Port = ([System.Net.IPEndPoint]$Listener.LocalEndpoint).Port
$Listener.Stop()

$PreviousSuppressDialogs = $env:PERF_MONITOR_SUPPRESS_DIALOGS
$env:PERF_MONITOR_SUPPRESS_DIALOGS = "1"
$Process = $null
try {
    $Arguments = @(
        "--port", $Port,
        "--no-browser",
        "--no-tray",
        "--exit-after-seconds", "15"
    )
    $Process = Start-Process `
        -FilePath $ExePath `
        -ArgumentList $Arguments `
        -PassThru `
        -WindowStyle Hidden

    $Health = $null
    $Deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $Deadline -and $null -eq $Health) {
        if ($Process.HasExited) {
            throw "EXE 在健康接口响应前退出，退出码：$($Process.ExitCode)"
        }
        try {
            $Health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$Port/api/v1/health" `
                -TimeoutSec 1
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    if ($null -eq $Health) {
        throw "EXE 启动后健康接口在期限内未响应。"
    }
    if ($Health.service -ne "perf-monitor") {
        throw "健康接口服务标识错误：$($Health.service)"
    }
    if ($Health.appVersion -ne "0.2.1") {
        throw "健康接口版本错误：$($Health.appVersion)"
    }
    if (-not $Health.instanceId) {
        throw "健康接口缺少 instanceId。"
    }

    Wait-Process -Id $Process.Id -Timeout 20
    $Process.Refresh()
    if (-not $Process.HasExited -or $Process.ExitCode -ne 0) {
        throw "EXE 未按预期正常退出。"
    }
    Write-Output (
        "冒烟测试通过：service={0} version={1} instanceId={2}" -f `
            $Health.service,
            $Health.appVersion,
            $Health.instanceId
    )
}
finally {
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    $env:PERF_MONITOR_SUPPRESS_DIALOGS = $PreviousSuppressDialogs
}
