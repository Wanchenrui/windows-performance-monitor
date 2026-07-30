[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentPath,

    [string]$DotnetHost = "",

    [string]$OutputPath = "",

    [ValidateRange(10, 120)]
    [int]$Samples = 20,

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [double]$CpuTolerancePoints = 3.0,

    [double]$MemoryTolerancePoints = 1.0,

    [ValidateRange(100, 5000)]
    [int]$MaxAlignmentMilliseconds = 1500
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
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
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
if ($DotnetHost) {
    $DotnetHost = (Resolve-Path -LiteralPath $DotnetHost).Path
}
$InstanceState = Join-Path $env:LOCALAPPDATA "PerfMonitor\instance.json"
$Token = [Guid]::NewGuid().ToString("N")
$AgentOutput = Join-Path $env:TEMP "perf-monitor-agent-$Token.jsonl"
$PythonStdout = Join-Path $env:TEMP "perf-monitor-python-$Token.stdout.log"
$PythonStderr = Join-Path $env:TEMP "perf-monitor-python-$Token.stderr.log"
$PythonProcess = $null
$AgentProcess = $null
$ServicePid = $null
$LatestPythonSnapshot = $null
$LatestAgentSnapshot = $null

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values,

        [Parameter(Mandatory = $true)]
        [ValidateRange(0.0, 1.0)]
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        throw "百分位计算至少需要一个样本。"
    }
    $Sorted = @($Values | Sort-Object)
    $Index = [Math]::Min(
        $Sorted.Count - 1,
        [Math]::Max(
            0,
            [Math]::Ceiling($Sorted.Count * $Percentile) - 1
        )
    )
    return [double]$Sorted[$Index]
}

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

function Get-Metric {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metrics,

        [Parameter(Mandatory = $true)]
        [string]$MetricId
    )

    $Property = $Metrics.PSObject.Properties[$MetricId]
    if ($null -eq $Property) {
        throw "快照缺少指标：$MetricId"
    }
    return $Property.Value
}

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

try {
    $PythonArguments = @(
        (Join-Path $ProjectRoot "app.py"),
        "--no-browser",
        "--no-tray",
        "--port",
        $Port.ToString(),
        "--sample-interval",
        "1",
        "--history-minutes",
        "1"
    )
    $PythonProcess = Start-Process `
        -FilePath $VenvPython `
        -ArgumentList $PythonArguments `
        -WorkingDirectory $ProjectRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $PythonStdout `
        -RedirectStandardError $PythonStderr `
        -PassThru

    $BaseUri = "http://127.0.0.1:$Port"
    $ReadyDeadline = (Get-Date).AddSeconds(20)
    $Health = $null
    do {
        Start-Sleep -Milliseconds 250
        try {
            $Health = Invoke-RestMethod `
                -Uri "$BaseUri/api/v1/health" `
                -TimeoutSec 1
        }
        catch {
            $Health = $null
        }
    } while (
        ($null -eq $Health -or $Health.service -ne "perf-monitor") -and
        (Get-Date) -lt $ReadyDeadline
    )
    if ($null -eq $Health -or $Health.service -ne "perf-monitor") {
        throw "Python oracle 在 20 秒内未就绪。"
    }

    if (Test-Path -LiteralPath $InstanceState) {
        $State = Get-Content -Raw -LiteralPath $InstanceState |
            ConvertFrom-Json
        if (
            $State.service -eq "perf-monitor" -and
            [int]$State.port -eq $Port
        ) {
            $ServicePid = [int]$State.pid
        }
    }
    if ($null -eq $ServicePid) {
        throw "无法确认本次 Python oracle 的服务 PID。"
    }

    $DurationSeconds = $Samples + 4
    $AgentArguments = @(
        "--duration-seconds",
        $DurationSeconds.ToString(),
        "--warmup-seconds",
        "2",
        "--output-period-ms",
        "1000",
        "--output",
        $AgentOutput
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

    $PythonSamples = [System.Collections.Generic.List[object]]::new()
    while (-not $AgentProcess.HasExited) {
        try {
            $Snapshot = Invoke-RestMethod `
                -Uri "$BaseUri/api/v1/snapshot" `
                -TimeoutSec 2
            $CpuMetrics = $Snapshot.groups.systemCpu.data.metrics
            $MemoryMetrics = $Snapshot.groups.memory.data.metrics
            $Cpu = $CpuMetrics."system.cpu.utilization.percent".value
            $Memory = $MemoryMetrics."system.memory.utilization.percent".value
            if ($null -ne $Cpu -and $null -ne $Memory) {
                $LatestPythonSnapshot = $Snapshot
                $PythonSamples.Add(
                    [pscustomobject]@{
                        time = [DateTimeOffset]::Parse(
                            $Snapshot.completedAtUtc
                        )
                        cpu = [double]$Cpu
                        memory = [double]$Memory
                    }
                )
            }
        }
        catch {
            # 单次 HTTP 读取失败由最小配对数门禁处理。
        }
        Start-Sleep -Milliseconds 1000
        $AgentProcess.Refresh()
    }
    $AgentProcess.WaitForExit()
    $AgentStdoutText = $AgentStdoutTask.GetAwaiter().GetResult()
    $AgentError = $AgentStderrTask.GetAwaiter().GetResult()
    if ($AgentProcess.ExitCode -ne 0) {
        throw "Agent 退出码 $($AgentProcess.ExitCode)：$AgentError"
    }

    $AgentSamples = [System.Collections.Generic.List[object]]::new()
    foreach ($Line in Get-Content -LiteralPath $AgentOutput) {
        $Snapshot = $Line | ConvertFrom-Json
        $LatestAgentSnapshot = $Snapshot
        $CpuMetrics = $Snapshot.groups.systemCpu.data.metrics
        $MemoryMetrics = $Snapshot.groups.memory.data.metrics
        $Cpu = $CpuMetrics."system.cpu.utilization.percent".value
        $Memory = $MemoryMetrics."system.memory.utilization.percent".value
        if ($null -ne $Cpu -and $null -ne $Memory) {
            $AgentSamples.Add(
                [pscustomobject]@{
                    sequence = [int]$Snapshot.sequence
                    time = [DateTimeOffset]::Parse(
                        $Snapshot.completedAtUtc
                    )
                    cpu = [double]$Cpu
                    memory = [double]$Memory
                }
            )
        }
    }

    $Pairs = [System.Collections.Generic.List[object]]::new()
    $UsedAgentSequences =
        [System.Collections.Generic.HashSet[int]]::new()
    foreach ($PythonSample in $PythonSamples) {
        $Nearest = $AgentSamples |
            Where-Object {
                -not $UsedAgentSequences.Contains([int]$_.sequence)
            } |
            Sort-Object {
                [Math]::Abs(($_.time - $PythonSample.time).TotalMilliseconds)
            } |
            Select-Object -First 1
        if ($null -eq $Nearest) {
            continue
        }

        $Alignment = [Math]::Abs(
            ($Nearest.time - $PythonSample.time).TotalMilliseconds
        )
        if ($Alignment -le $MaxAlignmentMilliseconds) {
            $null = $UsedAgentSequences.Add([int]$Nearest.sequence)
            $Pairs.Add(
                [pscustomobject]@{
                    pythonCpu = $PythonSample.cpu
                    agentCpu = $Nearest.cpu
                    pythonMemory = $PythonSample.memory
                    agentMemory = $Nearest.memory
                    alignmentMs = $Alignment
                }
            )
        }
    }

    $MinimumPairs = [Math]::Max(5, $Samples - 4)
    if ($Pairs.Count -lt $MinimumPairs) {
        throw "同窗配对不足：$($Pairs.Count) < $MinimumPairs。"
    }
    $Pairs = @($Pairs | Select-Object -Last $Samples)
    if (
        $null -eq $LatestPythonSnapshot -or
        $null -eq $LatestAgentSnapshot
    ) {
        throw "差分期间未获得两端完整快照。"
    }
    if (
        $LatestPythonSnapshot.contractVersion -ne "1.0" -or
        $LatestPythonSnapshot.productVersion -ne "0.3.0" -or
        $LatestAgentSnapshot.contractVersion -ne "1.0" -or
        $LatestAgentSnapshot.productVersion -ne $ExpectedAgentVersion
    ) {
        throw "差分快照的契约或产品版本不正确。"
    }

    $PythonCpuMean = [double]($Pairs |
        Measure-Object -Property pythonCpu -Average).Average
    $AgentCpuMean = [double]($Pairs |
        Measure-Object -Property agentCpu -Average).Average
    $PythonMemoryMean = [double]($Pairs |
        Measure-Object -Property pythonMemory -Average).Average
    $AgentMemoryMean = [double]($Pairs |
        Measure-Object -Property agentMemory -Average).Average
    $CpuDeviation = [Math]::Abs($PythonCpuMean - $AgentCpuMean)
    $MemoryDeviation = [Math]::Abs(
        $PythonMemoryMean - $AgentMemoryMean
    )
    $PythonCpuP95 = Get-Percentile `
        -Values ([double[]]$Pairs.pythonCpu) `
        -Percentile 0.95
    $AgentCpuP95 = Get-Percentile `
        -Values ([double[]]$Pairs.agentCpu) `
        -Percentile 0.95
    $PythonMemoryP95 = Get-Percentile `
        -Values ([double[]]$Pairs.pythonMemory) `
        -Percentile 0.95
    $AgentMemoryP95 = Get-Percentile `
        -Values ([double[]]$Pairs.agentMemory) `
        -Percentile 0.95

    $SemanticMismatches =
        [System.Collections.Generic.List[string]]::new()
    $SemanticComparisons = 0
    $CoreMetrics = @(
        [pscustomobject]@{
            group = "systemCpu"
            metric = "system.cpu.utilization.percent"
        },
        [pscustomobject]@{
            group = "systemCpu"
            metric = "system.cpu.logical_processor.count"
        },
        [pscustomobject]@{
            group = "memory"
            metric = "system.memory.utilization.percent"
        },
        [pscustomobject]@{
            group = "memory"
            metric = "system.memory.used.bytes"
        },
        [pscustomobject]@{
            group = "memory"
            metric = "system.memory.available.bytes"
        },
        [pscustomobject]@{
            group = "memory"
            metric = "system.memory.total.bytes"
        },
        [pscustomobject]@{
            group = "uptime"
            metric = "system.uptime.seconds"
        }
    )
    foreach ($Descriptor in $CoreMetrics) {
        $PythonGroupProperty =
            $LatestPythonSnapshot.groups.PSObject.Properties[
                $Descriptor.group
            ]
        $AgentGroupProperty =
            $LatestAgentSnapshot.groups.PSObject.Properties[
                $Descriptor.group
            ]
        $PythonGroup = $PythonGroupProperty.Value
        $AgentGroup = $AgentGroupProperty.Value
        $PythonMetrics = $PythonGroup.data.metrics
        $AgentMetrics = $AgentGroup.data.metrics
        $PythonMetric = Get-Metric `
            -Metrics $PythonMetrics `
            -MetricId $Descriptor.metric
        $AgentMetric = Get-Metric `
            -Metrics $AgentMetrics `
            -MetricId $Descriptor.metric
        $SemanticComparisons++
        if (
            $PythonMetric.unit -cne $AgentMetric.unit -or
            $PythonMetric.sourceId -cne $AgentMetric.sourceId
        ) {
            $SemanticMismatches.Add(
                "$($Descriptor.group)/$($Descriptor.metric)"
            )
        }
    }

    $AgentVolumesById = @{}
    foreach ($Volume in @($LatestAgentSnapshot.groups.volumes.data)) {
        $AgentVolumesById[[string]$Volume.volumeId] = $Volume
    }
    $VolumeMatches = 0
    $VolumeCapacityMismatches =
        [System.Collections.Generic.List[string]]::new()
    $VolumeMetricIds = @(
        "system.volume.utilization.percent",
        "system.volume.used.bytes",
        "system.volume.free.bytes",
        "system.volume.total.bytes"
    )
    foreach ($PythonVolume in @(
        $LatestPythonSnapshot.groups.volumes.data
    )) {
        $VolumeId = [string]$PythonVolume.volumeId
        if (-not $AgentVolumesById.ContainsKey($VolumeId)) {
            continue
        }
        $VolumeMatches++
        $AgentVolume = $AgentVolumesById[$VolumeId]
        $PythonTotal = Get-Metric `
            -Metrics $PythonVolume.metrics `
            -MetricId "system.volume.total.bytes"
        $AgentTotal = Get-Metric `
            -Metrics $AgentVolume.metrics `
            -MetricId "system.volume.total.bytes"
        if ([Int64]$PythonTotal.value -ne [Int64]$AgentTotal.value) {
            $VolumeCapacityMismatches.Add($VolumeId)
        }
        foreach ($MetricId in $VolumeMetricIds) {
            $PythonMetric = Get-Metric `
                -Metrics $PythonVolume.metrics `
                -MetricId $MetricId
            $AgentMetric = Get-Metric `
                -Metrics $AgentVolume.metrics `
                -MetricId $MetricId
            $SemanticComparisons++
            if (
                $PythonMetric.unit -cne $AgentMetric.unit -or
                $PythonMetric.sourceId -cne $AgentMetric.sourceId
            ) {
                $SemanticMismatches.Add(
                    "volumes/$VolumeId/$MetricId"
                )
            }
        }
    }
    if ($VolumeMatches -eq 0) {
        throw "Python/.NET 快照没有可比较的共同卷。"
    }

    $ProcessIdentityKeys =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal
        )
    $ProcessIdentityDuplicates =
        [System.Collections.Generic.List[string]]::new()
    $ProcessNullMismatches =
        [System.Collections.Generic.List[string]]::new()
    foreach ($ProcessSnapshot in @(
        $LatestPythonSnapshot,
        $LatestAgentSnapshot
    )) {
        $Language = if (
            $ProcessSnapshot.productVersion -eq "0.3.0"
        ) {
            "python"
        }
        else {
            "agent"
        }
        $LocalKeys =
            [System.Collections.Generic.HashSet[string]]::new(
                [StringComparer]::Ordinal
            )
        foreach ($ProcessRow in @(
            $ProcessSnapshot.groups.processes.data
        )) {
            $IdentityKey = "{0}:{1}" -f `
                [int]$ProcessRow.identity.pid,
                [Int64]$ProcessRow.identity.creationTimeTicks
            if (
                [int]$ProcessRow.identity.pid -le 0 -or
                [Int64]$ProcessRow.identity.creationTimeTicks -le 0 -or
                -not $LocalKeys.Add($IdentityKey)
            ) {
                $ProcessIdentityDuplicates.Add(
                    "$Language/$IdentityKey"
                )
            }
            if ($Language -eq "agent") {
                $null = $ProcessIdentityKeys.Add($IdentityKey)
            }

            $Normalized = Get-Metric `
                -Metrics $ProcessRow.metrics `
                -MetricId "process.cpu.normalized.percent"
            $CoreEquivalent = Get-Metric `
                -Metrics $ProcessRow.metrics `
                -MetricId "process.cpu.core_equivalent.percent"
            $HasCpuValues = (
                $null -ne $Normalized.value -and
                $null -ne $CoreEquivalent.value
            )
            if ([bool]$ProcessRow.cpuReady -ne $HasCpuValues) {
                $ProcessNullMismatches.Add(
                    "$Language/$IdentityKey"
                )
            }
        }
    }

    $CommonProcessIdentities = 0
    foreach ($PythonProcessRow in @(
        $LatestPythonSnapshot.groups.processes.data
    )) {
        $IdentityKey = "{0}:{1}" -f `
            [int]$PythonProcessRow.identity.pid,
            [Int64]$PythonProcessRow.identity.creationTimeTicks
        if (-not $ProcessIdentityKeys.Contains($IdentityKey)) {
            continue
        }
        $CommonProcessIdentities++
        $AgentProcessRow = @(
            $LatestAgentSnapshot.groups.processes.data |
                Where-Object {
                    [int]$_.identity.pid -eq
                        [int]$PythonProcessRow.identity.pid -and
                    [Int64]$_.identity.creationTimeTicks -eq
                        [Int64]$PythonProcessRow.identity.creationTimeTicks
                }
        )[0]
        foreach ($MetricId in @(
            "process.cpu.normalized.percent",
            "process.cpu.core_equivalent.percent",
            "process.memory.working_set.bytes",
            "process.memory.private.bytes"
        )) {
            $PythonMetric = Get-Metric `
                -Metrics $PythonProcessRow.metrics `
                -MetricId $MetricId
            $AgentMetric = Get-Metric `
                -Metrics $AgentProcessRow.metrics `
                -MetricId $MetricId
            $SemanticComparisons++
            if (
                $PythonMetric.unit -cne $AgentMetric.unit -or
                $PythonMetric.sourceId -cne $AgentMetric.sourceId
            ) {
                $SemanticMismatches.Add(
                    "processes/$IdentityKey/$MetricId"
                )
            }
        }
    }
    $SemanticsPassed = (
        $SemanticMismatches.Count -eq 0 -and
        $VolumeCapacityMismatches.Count -eq 0 -and
        $ProcessIdentityDuplicates.Count -eq 0 -and
        $ProcessNullMismatches.Count -eq 0 -and
        $CommonProcessIdentities -ge 3
    )
    $Result = [ordered]@{
        contractVersion = "1.0"
        pythonProductVersion = "0.3.0"
        agentProductVersion = $ExpectedAgentVersion
        pairs = $Pairs.Count
        alignment = [ordered]@{
            maximumMilliseconds = [Math]::Round(
                ($Pairs |
                    Measure-Object -Property alignmentMs -Maximum).Maximum,
                3
            )
            limitMilliseconds = $MaxAlignmentMilliseconds
        }
        cpu = [ordered]@{
            pythonMeanPct = [Math]::Round($PythonCpuMean, 3)
            agentMeanPct = [Math]::Round($AgentCpuMean, 3)
            deviationPoints = [Math]::Round($CpuDeviation, 3)
            tolerancePoints = $CpuTolerancePoints
            passed = $CpuDeviation -le $CpuTolerancePoints
            p95 = [ordered]@{
                pythonPct = [Math]::Round($PythonCpuP95, 3)
                agentPct = [Math]::Round($AgentCpuP95, 3)
                deviationPoints = [Math]::Round(
                    [Math]::Abs($PythonCpuP95 - $AgentCpuP95),
                    3
                )
            }
        }
        memory = [ordered]@{
            pythonMeanPct = [Math]::Round($PythonMemoryMean, 3)
            agentMeanPct = [Math]::Round($AgentMemoryMean, 3)
            deviationPoints = [Math]::Round($MemoryDeviation, 3)
            tolerancePoints = $MemoryTolerancePoints
            passed = $MemoryDeviation -le $MemoryTolerancePoints
            p95 = [ordered]@{
                pythonPct = [Math]::Round($PythonMemoryP95, 3)
                agentPct = [Math]::Round($AgentMemoryP95, 3)
                deviationPoints = [Math]::Round(
                    [Math]::Abs(
                        $PythonMemoryP95 - $AgentMemoryP95
                    ),
                    3
                )
            }
        }
        semantics = [ordered]@{
            passed = $SemanticsPassed
            unitsAndSources = [ordered]@{
                comparisons = $SemanticComparisons
                mismatches = @($SemanticMismatches)
            }
            volumeCapacity = [ordered]@{
                matchedVolumes = $VolumeMatches
                mismatches = @($VolumeCapacityMismatches)
            }
            processIdentity = [ordered]@{
                commonExactIdentities = $CommonProcessIdentities
                invalidOrDuplicate = @($ProcessIdentityDuplicates)
            }
            nullSemantics = [ordered]@{
                mismatches = @($ProcessNullMismatches)
            }
        }
    }
    $ResultJson = $Result | ConvertTo-Json -Depth 5
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

    if (
        $CpuDeviation -gt $CpuTolerancePoints -or
        $MemoryDeviation -gt $MemoryTolerancePoints -or
        -not $SemanticsPassed
    ) {
        throw "Python/.NET 数值差分或精确语义门禁失败。"
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
    if ($null -ne $ServicePid) {
        $ServiceProcess = Get-Process `
            -Id $ServicePid `
            -ErrorAction SilentlyContinue
        if ($null -ne $ServiceProcess) {
            Stop-Process -Id $ServicePid
            $null = $ServiceProcess.WaitForExit(5000)
        }
    }
    if ($null -ne $PythonProcess) {
        $LivePythonLauncher = Get-Process `
            -Id $PythonProcess.Id `
            -ErrorAction SilentlyContinue
        if ($null -ne $LivePythonLauncher) {
            Stop-Process -Id $LivePythonLauncher.Id
            $null = $LivePythonLauncher.WaitForExit(5000)
        }
    }
    if (Test-Path -LiteralPath $InstanceState) {
        $State = Get-Content -Raw -LiteralPath $InstanceState |
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
    foreach ($Path in @(
        $AgentOutput,
        $PythonStdout,
        $PythonStderr
    )) {
        if (Test-Path -LiteralPath $Path) {
            Remove-Item -LiteralPath $Path
        }
    }
}
