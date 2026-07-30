[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentPath,

    [string]$DotnetHost = "",

    [ValidateRange(15, 604800)]
    [int]$DurationSeconds = 259200,

    [ValidateRange(0, 300)]
    [int]$WarmupSeconds = 5,

    [ValidateRange(1, 300)]
    [int]$ProbeIntervalSeconds = 30,

    [ValidateRange(1, 3600)]
    [int]$SnapshotPeriodSeconds = 300,

    [ValidateRange(-1, 4096)]
    [double]$MaxWorkingSetMiB = -1,

    [ValidateRange(-1, 4096)]
    [double]$MaxPrivateMemoryMiB = -1,

    [ValidateRange(-1, 1024)]
    [double]$MaxRetainedGrowthMiB = -1,

    [ValidateRange(-1, 1024)]
    [double]$MaxGcHeapGrowthMiB = -1,

    [ValidateRange(-1, 100)]
    [double]$MaxCpuCoreEquivalentPct = -1,

    [ValidateRange(-1, 65536)]
    [int]$MaxHandleCount = -1,

    [ValidateRange(-1, 65536)]
    [int]$MaxRetainedHandleGrowth = -1,

    [ValidateRange(-1, 4096)]
    [int]$MaxThreadCount = -1,

    [string]$BudgetPath = "",

    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")
$BuildProperties = [xml](
    Get-Content -LiteralPath (
        Join-Path $ProjectRoot "Directory.Build.props"
    ) -Raw
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "Directory.Build.props 缺少产品版本。"
}
$ExpectedAgentVersion = $VersionNode.InnerText.Trim()
$BudgetPath = if ($BudgetPath) {
    (Resolve-Path -LiteralPath $BudgetPath).Path
}
else {
    Join-Path $ProjectRoot "release\resource-budgets-v1.json"
}
$Budget = Get-Content -LiteralPath $BudgetPath -Raw |
    ConvertFrom-Json
if ($Budget.schemaVersion -ne "1.0" -or $null -eq $Budget.agent) {
    throw "资源预算文件无效。"
}
if ($MaxWorkingSetMiB -lt 0) {
    $MaxWorkingSetMiB = [double]$Budget.agent.workingSetPeakMiB
}
if ($MaxPrivateMemoryMiB -lt 0) {
    $MaxPrivateMemoryMiB = [double]$Budget.agent.privateMemoryPeakMiB
}
if ($MaxRetainedGrowthMiB -lt 0) {
    $MaxRetainedGrowthMiB = [double](
        $Budget.agent.retainedPrivateGrowthMiB
    )
}
if ($MaxGcHeapGrowthMiB -lt 0) {
    $MaxGcHeapGrowthMiB = [double]$Budget.agent.gcHeapGrowthMiB
}
if ($MaxCpuCoreEquivalentPct -lt 0) {
    $MaxCpuCoreEquivalentPct = [double](
        $Budget.agent.cpuCoreEquivalentMeanPct
    )
}
if ($MaxHandleCount -lt 0) {
    $MaxHandleCount = [int]$Budget.agent.handlePeak
}
if ($MaxRetainedHandleGrowth -lt 0) {
    $MaxRetainedHandleGrowth = [int](
        $Budget.agent.retainedHandleGrowth
    )
}
if ($MaxThreadCount -lt 0) {
    $MaxThreadCount = [int]$Budget.agent.threadPeak
}
if (
    $MaxWorkingSetMiB -le 0 -or
    $MaxPrivateMemoryMiB -le 0 -or
    $MaxRetainedGrowthMiB -lt 0 -or
    $MaxGcHeapGrowthMiB -lt 0 -or
    $MaxCpuCoreEquivalentPct -le 0 -or
    $MaxHandleCount -le 0 -or
    $MaxRetainedHandleGrowth -lt 0 -or
    $MaxThreadCount -le 0
) {
    throw "资源预算必须是有效的正门限。"
}
$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
$AgentSha256 = Get-PerfMonitorSha256 -Path $AgentPath
if ($DotnetHost) {
    $DotnetHost = (Resolve-Path -LiteralPath $DotnetHost).Path
}
if ($DurationSeconds -lt 3 * $ProbeIntervalSeconds) {
    throw "DurationSeconds 至少应为 ProbeIntervalSeconds 的 3 倍。"
}

$Token = [Guid]::NewGuid().ToString("N")
$SnapshotOutput = Join-Path `
    $env:TEMP `
    "perf-monitor-agent-soak-$Token.jsonl"
$AgentProcess = $null
$StartedAt = [DateTimeOffset]::UtcNow
$Rows = [System.Collections.Generic.List[object]]::new()

function ConvertTo-NativeArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $Escaped = [Regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $Escaped = [Regex]::Replace($Escaped, '(\\+)$', '$1$1')
    return '"' + $Escaped + '"'
}

function Get-Median {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values
    )

    if ($Values.Count -eq 0) {
        throw "中位数计算至少需要一个样本。"
    }
    $Sorted = @($Values | Sort-Object)
    $Middle = [Math]::Floor($Sorted.Count / 2)
    if ($Sorted.Count % 2 -eq 1) {
        return [double]$Sorted[$Middle]
    }
    return (
        [double]$Sorted[$Middle - 1] +
        [double]$Sorted[$Middle]
    ) / 2
}

function Get-LinearSlopePerHour {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Samples
    )

    if ($Samples.Count -lt 2) {
        return 0.0
    }
    [double]$SumX = 0
    [double]$SumY = 0
    [double]$SumXy = 0
    [double]$SumX2 = 0
    foreach ($Sample in $Samples) {
        $X = [double]$Sample.elapsedSeconds
        $Y = [double]$Sample.privateMemoryMiB
        $SumX += $X
        $SumY += $Y
        $SumXy += $X * $Y
        $SumX2 += $X * $X
    }
    $Count = [double]$Samples.Count
    $Denominator = $Count * $SumX2 - $SumX * $SumX
    if ([Math]::Abs($Denominator) -lt 0.000001) {
        return 0.0
    }
    return 3600 * (
        ($Count * $SumXy - $SumX * $SumY) / $Denominator
    )
}

try {
    $AgentArguments = @(
        "--quiet",
        "--duration-seconds",
        $DurationSeconds.ToString(
            [Globalization.CultureInfo]::InvariantCulture
        ),
        "--warmup-seconds",
        $WarmupSeconds.ToString(
            [Globalization.CultureInfo]::InvariantCulture
        ),
        "--output-period-ms",
        (
            1000 * $SnapshotPeriodSeconds
        ).ToString([Globalization.CultureInfo]::InvariantCulture),
        "--output",
        $SnapshotOutput
    )
    $AgentExecutable = $AgentPath
    if ($DotnetHost) {
        $AgentExecutable = $DotnetHost
        $AgentArguments = @($AgentPath) + $AgentArguments
    }

    $AgentStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $AgentStartInfo.FileName = $AgentExecutable
    $AgentStartInfo.Arguments = (
        $AgentArguments |
            ForEach-Object {
                ConvertTo-NativeArgument -Value $_
            }
    ) -join " "
    $AgentStartInfo.WorkingDirectory = $ProjectRoot
    $AgentStartInfo.UseShellExecute = $false
    $AgentStartInfo.CreateNoWindow = $true
    $AgentStartInfo.RedirectStandardOutput = $true
    $AgentStartInfo.RedirectStandardError = $true
    $AgentProcess = [System.Diagnostics.Process]::new()
    $AgentProcess.StartInfo = $AgentStartInfo
    if (-not $AgentProcess.Start()) {
        throw "无法启动 .NET Agent。"
    }
    $AgentStdoutTask = $AgentProcess.StandardOutput.ReadToEndAsync()
    $AgentStderrTask = $AgentProcess.StandardError.ReadToEndAsync()
    $StartedAt = [DateTimeOffset]::UtcNow
    $HardDeadline = $StartedAt.AddSeconds(
        $WarmupSeconds + $DurationSeconds + 30
    )

    while (-not $AgentProcess.HasExited) {
        Start-Sleep -Seconds $ProbeIntervalSeconds
        $AgentProcess.Refresh()
        if ($AgentProcess.HasExited) {
            break
        }
        $Now = [DateTimeOffset]::UtcNow
        if ($Now -gt $HardDeadline) {
            throw "Agent 超过预期结束时间 30 秒仍未退出。"
        }
        $AgentProcess.Refresh()
        $Rows.Add(
            [pscustomobject]@{
                elapsedSeconds = ($Now - $StartedAt).TotalSeconds
                workingSetMiB = (
                    $AgentProcess.WorkingSet64 / 1MB
                )
                privateMemoryMiB = (
                    $AgentProcess.PrivateMemorySize64 / 1MB
                )
                totalProcessorSeconds = (
                    $AgentProcess.TotalProcessorTime.TotalSeconds
                )
                handleCount = $AgentProcess.HandleCount
                threadCount = $AgentProcess.Threads.Count
            }
        )
    }

    $AgentProcess.WaitForExit()
    $null = $AgentStdoutTask.GetAwaiter().GetResult()
    $AgentError = $AgentStderrTask.GetAwaiter().GetResult()
    $CompletedAt = [DateTimeOffset]::UtcNow
    $ElapsedSeconds = ($CompletedAt - $StartedAt).TotalSeconds
    if ($AgentProcess.ExitCode -ne 0) {
        throw "Agent 退出码 $($AgentProcess.ExitCode)：$AgentError"
    }
    $MinimumExpectedSeconds =
        $WarmupSeconds + $DurationSeconds - 2
    if ($ElapsedSeconds -lt $MinimumExpectedSeconds) {
        throw (
            "Agent 提前退出：{0:N3}s < {1:N3}s。" -f
            $ElapsedSeconds,
            $MinimumExpectedSeconds
        )
    }
    if (-not (Test-Path -LiteralPath $SnapshotOutput)) {
        throw "Agent 未生成长稳快照证据。"
    }

    $PreviousSequence = -1L
    $SnapshotCount = 0
    $FirstSequence = $null
    $LastSequence = $null
    $GcHeapValues = [System.Collections.Generic.List[double]]::new()
    $SnapshotReader = [System.IO.File]::OpenText($SnapshotOutput)
    try {
        while ($null -ne ($Line = $SnapshotReader.ReadLine())) {
            if ([string]::IsNullOrWhiteSpace($Line)) {
                continue
            }
            $Snapshot = $Line | ConvertFrom-Json
            if (
                $Snapshot.contractVersion -ne "1.0" -or
                $Snapshot.productVersion -ne $ExpectedAgentVersion
            ) {
                throw "长稳输出的契约或产品版本不正确。"
            }
            if ([Int64]$Snapshot.sequence -le $PreviousSequence) {
                throw "长稳输出的 sequence 未严格递增。"
            }
            $PreviousSequence = [Int64]$Snapshot.sequence
            if ($null -eq $FirstSequence) {
                $FirstSequence = $PreviousSequence
            }
            $LastSequence = $PreviousSequence
            $SnapshotCount++

            $SelfMetrics = $Snapshot.groups.self.data.metrics
            $GcProperty = $SelfMetrics.PSObject.Properties[
                "agent.gc.heap.bytes"
            ]
            if (
                $null -eq $GcProperty -or
                $null -eq $GcProperty.Value.value
            ) {
                throw "长稳快照缺少 Agent GC heap 指标。"
            }
            $GcHeapValues.Add(
                [double]$GcProperty.Value.value / 1MB
            )
        }
    }
    finally {
        $SnapshotReader.Dispose()
    }
    $MaximumSnapshots = [Math]::Ceiling(
        $DurationSeconds / [double]$SnapshotPeriodSeconds
    ) + 3
    if (
        $SnapshotCount -lt 2 -or
        $SnapshotCount -gt $MaximumSnapshots
    ) {
        throw (
            "长稳快照数量异常：{0}，允许范围 2..{1}。" -f
            $SnapshotCount,
            $MaximumSnapshots
        )
    }

    $SteadyRows = @(
        $Rows |
            Where-Object {
                $_.elapsedSeconds -ge (
                    $WarmupSeconds + $ProbeIntervalSeconds
                )
            }
    )
    if ($SteadyRows.Count -lt 3) {
        throw "稳态资源样本不足：$($SteadyRows.Count) < 3。"
    }
    $WindowCount = [Math]::Max(
        1,
        [Math]::Floor($SteadyRows.Count * 0.2)
    )
    $Head = @($SteadyRows | Select-Object -First $WindowCount)
    $Tail = @($SteadyRows | Select-Object -Last $WindowCount)
    $HeadPrivateMedian = Get-Median `
        -Values ([double[]]$Head.privateMemoryMiB)
    $TailPrivateMedian = Get-Median `
        -Values ([double[]]$Tail.privateMemoryMiB)
    $RetainedGrowth = $TailPrivateMedian - $HeadPrivateMedian
    $PeakWorkingSet = [double](
        $SteadyRows |
            Measure-Object -Property workingSetMiB -Maximum
    ).Maximum
    $PeakPrivateMemory = [double](
        $SteadyRows |
            Measure-Object -Property privateMemoryMiB -Maximum
    ).Maximum
    $PeakHandleCount = [int](
        $SteadyRows |
            Measure-Object -Property handleCount -Maximum
    ).Maximum
    $HeadHandleMedian = Get-Median `
        -Values ([double[]]$Head.handleCount)
    $TailHandleMedian = Get-Median `
        -Values ([double[]]$Tail.handleCount)
    $RetainedHandleGrowth =
        $TailHandleMedian - $HeadHandleMedian
    $PeakThreadCount = [int](
        $SteadyRows |
            Measure-Object -Property threadCount -Maximum
    ).Maximum
    $PrivateSlope = Get-LinearSlopePerHour -Samples $SteadyRows

    if ($GcHeapValues.Count -lt 2) {
        throw "Agent 自身 GC heap 样本不足。"
    }
    $GcHeapGrowth =
        $GcHeapValues[$GcHeapValues.Count - 1] - $GcHeapValues[0]

    $CpuCoreEquivalentMean = 0.0
    if ($SteadyRows.Count -ge 2) {
        $FirstCpuRow = $SteadyRows[0]
        $LastCpuRow = $SteadyRows[$SteadyRows.Count - 1]
        $CpuElapsed = (
            [double]$LastCpuRow.elapsedSeconds -
            [double]$FirstCpuRow.elapsedSeconds
        )
        if ($CpuElapsed -gt 0) {
            $CpuCoreEquivalentMean = 100 * (
                [double]$LastCpuRow.totalProcessorSeconds -
                [double]$FirstCpuRow.totalProcessorSeconds
            ) / $CpuElapsed
        }
    }

    $ResourcesPassed = (
        $PeakWorkingSet -le $MaxWorkingSetMiB -and
        $PeakPrivateMemory -le $MaxPrivateMemoryMiB -and
        $RetainedGrowth -le $MaxRetainedGrowthMiB -and
        $GcHeapGrowth -le $MaxGcHeapGrowthMiB -and
        $CpuCoreEquivalentMean -le $MaxCpuCoreEquivalentPct -and
        $PeakHandleCount -le $MaxHandleCount -and
        $RetainedHandleGrowth -le
            $MaxRetainedHandleGrowth -and
        $PeakThreadCount -le $MaxThreadCount
    )
    $IsRelease72HourRun = $DurationSeconds -ge 72 * 60 * 60
    $ReleaseWallClockPassed = (
        $IsRelease72HourRun -and
        $ElapsedSeconds -ge 72 * 60 * 60
    )
    $OverallPassed = (
        $ResourcesPassed -and
        (
            -not $IsRelease72HourRun -or
            $ReleaseWallClockPassed
        )
    )
    $Result = [ordered]@{
        contractVersion = "1.0"
        productVersion = $ExpectedAgentVersion
        agentSha256 = $AgentSha256
        profile = if ($IsRelease72HourRun) {
            "release-72h"
        }
        else {
            "accelerated-ci"
        }
        startedAtUtc = $StartedAt.ToString("O")
        completedAtUtc = $CompletedAt.ToString("O")
        requestedDurationSeconds = $DurationSeconds
        actualElapsedSeconds = [Math]::Round($ElapsedSeconds, 3)
        release72HourGate = [ordered]@{
            requiredDurationSeconds = 72 * 60 * 60
            actualWallClockPassed = (
                $ReleaseWallClockPassed -and
                $ResourcesPassed
            )
        }
        snapshots = [ordered]@{
            count = $SnapshotCount
            firstSequence = [Int64]$FirstSequence
            lastSequence = [Int64]$LastSequence
            maximumAllowed = $MaximumSnapshots
            passed = $true
        }
        resources = [ordered]@{
            samples = $SteadyRows.Count
            workingSet = [ordered]@{
                peakMiB = [Math]::Round($PeakWorkingSet, 3)
                limitMiB = $MaxWorkingSetMiB
            }
            privateMemory = [ordered]@{
                peakMiB = [Math]::Round($PeakPrivateMemory, 3)
                limitMiB = $MaxPrivateMemoryMiB
                headMedianMiB = [Math]::Round(
                    $HeadPrivateMedian,
                    3
                )
                tailMedianMiB = [Math]::Round(
                    $TailPrivateMedian,
                    3
                )
                retainedGrowthMiB = [Math]::Round(
                    $RetainedGrowth,
                    3
                )
                growthLimitMiB = $MaxRetainedGrowthMiB
                regressionSlopeMiBPerHour = [Math]::Round(
                    $PrivateSlope,
                    3
                )
            }
            gcHeap = [ordered]@{
                firstMiB = [Math]::Round($GcHeapValues[0], 3)
                lastMiB = [Math]::Round(
                    $GcHeapValues[$GcHeapValues.Count - 1],
                    3
                )
                growthMiB = [Math]::Round($GcHeapGrowth, 3)
                growthLimitMiB = $MaxGcHeapGrowthMiB
            }
            cpuCoreEquivalentMeanPct = [Math]::Round(
                $CpuCoreEquivalentMean,
                3
            )
            cpuCoreEquivalentLimitPct = $MaxCpuCoreEquivalentPct
            handlePeak = $PeakHandleCount
            handleLimit = $MaxHandleCount
            handleHeadMedian = [Math]::Round(
                $HeadHandleMedian,
                3
            )
            handleTailMedian = [Math]::Round(
                $TailHandleMedian,
                3
            )
            retainedHandleGrowth = [Math]::Round(
                $RetainedHandleGrowth,
                3
            )
            retainedHandleGrowthLimit =
                $MaxRetainedHandleGrowth
            threadPeak = $PeakThreadCount
            threadLimit = $MaxThreadCount
            passed = $ResourcesPassed
        }
        passed = $OverallPassed
    }
    $ResultJson = $Result | ConvertTo-Json -Depth 6
    if ($OutputPath) {
        $ResolvedOutputPath = if (
            [System.IO.Path]::IsPathRooted($OutputPath)
        ) {
            [System.IO.Path]::GetFullPath($OutputPath)
        }
        else {
            [System.IO.Path]::GetFullPath(
                (Join-Path $ProjectRoot $OutputPath)
            )
        }
        $OutputDirectory = Split-Path -Parent $ResolvedOutputPath
        if (-not (Test-Path -LiteralPath $OutputDirectory)) {
            $null = New-Item `
                -ItemType Directory `
                -Path $OutputDirectory `
                -Force
        }
        Set-Content `
            -LiteralPath $ResolvedOutputPath `
            -Value $ResultJson `
            -Encoding UTF8
    }
    $ResultJson

    if (-not $OverallPassed) {
        throw "Agent 长稳资源预算或增长门禁失败。"
    }
}
finally {
    if ($null -ne $AgentProcess) {
        $LiveAgent = Get-Process `
            -Id $AgentProcess.Id `
            -ErrorAction SilentlyContinue
        if ($null -ne $LiveAgent) {
            Stop-Process -Id $LiveAgent.Id -Force
            $null = $LiveAgent.WaitForExit(5000)
        }
        $AgentProcess.Dispose()
    }
    if (Test-Path -LiteralPath $SnapshotOutput) {
        Remove-Item -LiteralPath $SnapshotOutput
    }
}
