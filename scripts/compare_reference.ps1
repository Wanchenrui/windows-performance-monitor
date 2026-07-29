[CmdletBinding()]
param(
    [ValidateRange(10, 120)]
    [int]$Samples = 10,

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [double]$CpuTolerancePoints = 3.0,

    [double]$MemoryTolerancePoints = 1.0
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$InstanceState = Join-Path $env:LOCALAPPDATA "PerfMonitor\instance.json"
$ServicePid = $null
if (-not (Test-Path -LiteralPath $VenvPython)) {
    throw "未找到 .venv。请先运行 scripts\setup.ps1。"
}

if ($Port -eq 0) {
    $Listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0
    )
    $Listener.Start()
    $Port = ([System.Net.IPEndPoint]$Listener.LocalEndpoint).Port
    $Listener.Stop()
}

$Arguments = @(
    "app.py",
    "--no-browser",
    "--no-tray",
    "--port",
    $Port.ToString(),
    "--sample-interval",
    "1"
)
$AppProcess = Start-Process `
    -FilePath $VenvPython `
    -ArgumentList $Arguments `
    -WorkingDirectory $ProjectRoot `
    -WindowStyle Hidden `
    -PassThru
$AppPid = [int]$AppProcess.Id

$CpuCounter = $null
$MemoryCounter = $null
try {
    $StatsUri = "http://127.0.0.1:$Port/api/stats"
    $ReadyDeadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100
        try {
            $InitialStats = Invoke-RestMethod -Uri $StatsUri -TimeoutSec 1
        }
        catch {
            $InitialStats = $null
        }
    } while (
        ($null -eq $InitialStats -or $InitialStats.sequence -lt 1) -and
        (Get-Date) -lt $ReadyDeadline
    )
    if ($null -eq $InitialStats -or $InitialStats.sequence -lt 1) {
        throw "监控服务在 15 秒内未产生首个样本。"
    }
    if (Test-Path -LiteralPath $InstanceState) {
        $ReadyState = Get-Content -LiteralPath $InstanceState -Raw |
            ConvertFrom-Json
        if (
            $ReadyState.service -eq "perf-monitor" -and
            [int]$ReadyState.port -eq $Port
        ) {
            # Windows venv 启动器可能与实际解释器使用不同 PID。
            $ServicePid = [int]$ReadyState.pid
        }
    }
    if ($null -eq $ServicePid) {
        throw "无法确认本次参考测试的服务 PID。"
    }

    $CpuCounter = [System.Diagnostics.PerformanceCounter]::new(
        "Processor",
        "% Processor Time",
        "_Total"
    )
    $MemoryCounter = [System.Diagnostics.PerformanceCounter]::new(
        "Memory",
        "Available MBytes"
    )
    $null = $CpuCounter.NextValue()

    $Rows = @()
    $LastSequence = [int]$InitialStats.sequence
    for ($Index = 0; $Index -lt $Samples; $Index++) {
        Start-Sleep -Milliseconds 1000
        $ReferenceCpu = [double]$CpuCounter.NextValue()
        $ReferenceAvailableBytes = [double]$MemoryCounter.NextValue() * 1MB
        $Stats = Invoke-RestMethod -Uri $StatsUri -TimeoutSec 2

        if ([int]$Stats.sequence -le $LastSequence) {
            throw "API 返回了重复样本序号 $($Stats.sequence)。"
        }
        $LastSequence = [int]$Stats.sequence
        $TotalBytes = [double]$Stats.overview.memory.totalBytes
        $ReferenceMemory = 100.0 * (
            $TotalBytes - $ReferenceAvailableBytes
        ) / $TotalBytes

        $Rows += [pscustomobject]@{
            sequence = [int]$Stats.sequence
            apiCpuPct = [double]$Stats.overview.cpu.percent
            referenceCpuPct = $ReferenceCpu
            apiMemoryPct = [double]$Stats.overview.memory.percent
            referenceMemoryPct = $ReferenceMemory
            jitterMs = [double]$Stats.sample.jitterMs
            durationMs = [double]$Stats.sample.durationMs
        }
    }

    $ApiCpuMean = [double]($Rows |
        Measure-Object -Property apiCpuPct -Average).Average
    $ReferenceCpuMean = [double]($Rows |
        Measure-Object -Property referenceCpuPct -Average).Average
    $ApiMemoryMean = [double]($Rows |
        Measure-Object -Property apiMemoryPct -Average).Average
    $ReferenceMemoryMean = [double]($Rows |
        Measure-Object -Property referenceMemoryPct -Average).Average
    $CpuDeviation = [math]::Abs($ApiCpuMean - $ReferenceCpuMean)
    $MemoryDeviation = [math]::Abs(
        $ApiMemoryMean - $ReferenceMemoryMean
    )
    $SortedJitter = @($Rows.jitterMs | Sort-Object)
    $P95Index = [math]::Min(
        $SortedJitter.Count - 1,
        [math]::Ceiling($SortedJitter.Count * 0.95) - 1
    )

    $Result = [ordered]@{
        samples = $Samples
        cpu = [ordered]@{
            apiMeanPct = [math]::Round($ApiCpuMean, 3)
            referenceMeanPct = [math]::Round($ReferenceCpuMean, 3)
            deviationPoints = [math]::Round($CpuDeviation, 3)
            tolerancePoints = $CpuTolerancePoints
            passed = $CpuDeviation -le $CpuTolerancePoints
        }
        memory = [ordered]@{
            apiMeanPct = [math]::Round($ApiMemoryMean, 3)
            referenceMeanPct = [math]::Round($ReferenceMemoryMean, 3)
            deviationPoints = [math]::Round($MemoryDeviation, 3)
            tolerancePoints = $MemoryTolerancePoints
            passed = $MemoryDeviation -le $MemoryTolerancePoints
        }
        scheduler = [ordered]@{
            p95JitterMs = $SortedJitter[$P95Index]
            maxDurationMs = ($Rows |
                Measure-Object -Property durationMs -Maximum).Maximum
        }
    }
    $Result | ConvertTo-Json -Depth 4

    if (
        $CpuDeviation -gt $CpuTolerancePoints -or
        $MemoryDeviation -gt $MemoryTolerancePoints
    ) {
        throw "指标与 Windows 参考计数器的偏差超过门限。"
    }
}
finally {
    if ($null -ne $CpuCounter) {
        $CpuCounter.Dispose()
    }
    if ($null -ne $MemoryCounter) {
        $MemoryCounter.Dispose()
    }
    $ServiceProcess = Get-Process -Id $ServicePid -ErrorAction SilentlyContinue
    if ($null -ne $ServiceProcess) {
        Stop-Process -Id $ServicePid
        if (-not $ServiceProcess.WaitForExit(5000)) {
            throw "服务进程 $ServicePid 在 5 秒内未退出。"
        }
    }
    if ($null -ne $AppProcess -and -not $AppProcess.HasExited) {
        Stop-Process -Id $AppPid
        if (-not $AppProcess.WaitForExit(5000)) {
            throw "测试进程 $AppPid 在 5 秒内未退出。"
        }
    }
    if (Test-Path -LiteralPath $InstanceState) {
        $State = Get-Content -LiteralPath $InstanceState -Raw |
            ConvertFrom-Json
        if (
            [int]$State.pid -eq $ServicePid -and
            $null -eq (
                Get-Process -Id $ServicePid -ErrorAction SilentlyContinue
            )
        ) {
            Remove-Item -LiteralPath $InstanceState
        }
    }
}
