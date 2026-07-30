[CmdletBinding()]
param(
    [string]$AgentPath = "",

    [string]$DesktopPath = "",

    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 15
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $AgentPath) {
    $AgentPath = Join-Path `
        $ProjectRoot `
        "dist\agent\perf-monitor-agent.exe"
}
if (-not $DesktopPath) {
    $DesktopPath = Join-Path `
        $ProjectRoot `
        "dist\desktop\perf-monitor-desktop.exe"
}
$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
$DesktopPath = (Resolve-Path -LiteralPath $DesktopPath).Path

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
$ExpectedVersion = $VersionNode.InnerText.Trim()

$Token = [Guid]::NewGuid().ToString("N")
$TempBase = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()
)
$DataRoot = Join-Path $TempBase "perf-monitor-smoke-$Token"
$SnapshotPath = Join-Path $DataRoot "snapshots.jsonl"
$DatabasePath = Join-Path $DataRoot "history-v1.db"
$AgentProcess = $null
$DesktopProcess = $null

function Get-LatestSnapshot {
    if (-not (Test-Path -LiteralPath $SnapshotPath)) {
        return $null
    }
    $Line = Get-Content -LiteralPath $SnapshotPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Last 1
    if (-not $Line) {
        return $null
    }
    try {
        return $Line | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Assert-CoreSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Snapshot
    )

    if ($Snapshot.contractVersion -ne "1.0") {
        throw "Agent 冒烟快照的契约版本错误。"
    }
    if ($Snapshot.productVersion -ne $ExpectedVersion) {
        throw "Agent 冒烟快照的产品版本错误。"
    }
    if (-not $Snapshot.instanceId) {
        throw "Agent 冒烟快照缺少 instanceId。"
    }
    foreach ($Group in @(
        "systemCpu",
        "memory",
        "network",
        "diskIo",
        "power",
        "volumes",
        "uptime",
        "processes",
        "sampler",
        "self"
    )) {
        if (
            $Snapshot.groups.PSObject.Properties.Name -notcontains
                $Group
        ) {
            throw "Agent 冒烟快照缺少指标组：$Group"
        }
    }
}

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
try {
    $AgentArguments = @(
        "--quiet",
        "--warmup-seconds",
        "1",
        "--duration-seconds",
        $DurationSeconds,
        "--output-period-ms",
        "500",
        "--data-directory",
        "`"$DataRoot`"",
        "--output",
        "`"$SnapshotPath`""
    )
    $AgentProcess = Start-Process `
        -FilePath $AgentPath `
        -ArgumentList $AgentArguments `
        -PassThru `
        -WindowStyle Hidden

    $FirstSnapshot = $null
    $ReadyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(8)
    while (
        [DateTimeOffset]::UtcNow -lt $ReadyDeadline -and
        $null -eq $FirstSnapshot
    ) {
        $AgentProcess.Refresh()
        if ($AgentProcess.HasExited) {
            throw "Agent 在 IPC/SQLite 就绪前退出，退出码：$($AgentProcess.ExitCode)"
        }
        if (Test-Path -LiteralPath $DatabasePath) {
            $FirstSnapshot = Get-LatestSnapshot
        }
        if ($null -eq $FirstSnapshot) {
            Start-Sleep -Milliseconds 100
        }
    }
    if ($null -eq $FirstSnapshot) {
        throw "Agent 未在期限内产生 SQLite 数据库和实时快照。"
    }
    Assert-CoreSnapshot -Snapshot $FirstSnapshot

    $DesktopProcess = Start-Process `
        -FilePath $DesktopPath `
        -PassThru `
        -WindowStyle Minimized
    Start-Sleep -Seconds 2
    $DesktopProcess.Refresh()
    if ($DesktopProcess.HasExited) {
        throw "Desktop 在生命周期检查前退出，退出码：$($DesktopProcess.ExitCode)"
    }
    $AgentProcess.Refresh()
    if ($AgentProcess.HasExited) {
        throw "Desktop 启动后 Agent 意外退出。"
    }

    $SequenceBeforeDesktopExit = [Int64](
        (Get-LatestSnapshot).sequence
    )
    if (-not $DesktopProcess.CloseMainWindow()) {
        Stop-Process -Id $DesktopProcess.Id -Force
    }
    elseif (-not $DesktopProcess.WaitForExit(5000)) {
        Stop-Process -Id $DesktopProcess.Id -Force
    }
    $DesktopProcess.WaitForExit()

    $AgentProcess.Refresh()
    if ($AgentProcess.HasExited) {
        throw "Desktop 退出影响了 Agent 生命周期。"
    }

    $ContinuedSnapshot = $null
    $ContinueDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (
        [DateTimeOffset]::UtcNow -lt $ContinueDeadline -and
        (
            $null -eq $ContinuedSnapshot -or
            [Int64]$ContinuedSnapshot.sequence -le
                $SequenceBeforeDesktopExit
        )
    ) {
        Start-Sleep -Milliseconds 100
        $ContinuedSnapshot = Get-LatestSnapshot
    }
    if (
        $null -eq $ContinuedSnapshot -or
        [Int64]$ContinuedSnapshot.sequence -le
            $SequenceBeforeDesktopExit
    ) {
        throw "Desktop 退出后 Agent 未继续发布快照。"
    }

    if (-not $AgentProcess.WaitForExit(
        ($DurationSeconds + 10) * 1000
    )) {
        throw "Agent 未按 --duration-seconds 正常退出。"
    }
    if ($AgentProcess.ExitCode -ne 0) {
        throw "Agent 冒烟退出码错误：$($AgentProcess.ExitCode)"
    }

    $FinalSnapshot = Get-LatestSnapshot
    if ($null -eq $FinalSnapshot) {
        throw "Agent 冒烟结束后缺少最终快照。"
    }
    Assert-CoreSnapshot -Snapshot $FinalSnapshot
    $Database = Get-Item -LiteralPath $DatabasePath
    if ($Database.Length -le 0) {
        throw "SQLite 历史数据库为空。"
    }

    Write-Output (
        "冒烟测试通过：version={0} instanceId={1} " +
        "sequence={2}->{3} databaseBytes={4}" -f `
            $FinalSnapshot.productVersion,
            $FinalSnapshot.instanceId,
            $SequenceBeforeDesktopExit,
            $FinalSnapshot.sequence,
            $Database.Length
    )
}
finally {
    if (
        $null -ne $DesktopProcess -and
        -not $DesktopProcess.HasExited
    ) {
        Stop-Process `
            -Id $DesktopProcess.Id `
            -Force `
            -ErrorAction SilentlyContinue
    }
    if (
        $null -ne $AgentProcess -and
        -not $AgentProcess.HasExited
    ) {
        Stop-Process `
            -Id $AgentProcess.Id `
            -Force `
            -ErrorAction SilentlyContinue
    }

    $DataRootFull = [System.IO.Path]::GetFullPath($DataRoot)
    $TempPrefix = $TempBase.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if (
        $DataRootFull.StartsWith(
            $TempPrefix,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        (Split-Path -Leaf $DataRootFull).StartsWith(
            "perf-monitor-smoke-",
            [StringComparison]::Ordinal
        ) -and
        (Test-Path -LiteralPath $DataRootFull)
    ) {
        Remove-Item `
            -LiteralPath $DataRootFull `
            -Recurse `
            -Force
    }
}
