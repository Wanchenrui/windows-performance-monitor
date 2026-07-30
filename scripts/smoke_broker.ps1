[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BrokerPath
)

$ErrorActionPreference = "Stop"
$BrokerFullPath = (Resolve-Path -LiteralPath $BrokerPath).Path
$ExpectedFileName = "perf-monitor-broker.exe"
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
    [System.IO.Path]::GetFileName($BrokerFullPath),
    $ExpectedFileName
)) {
    throw "Broker 路径不是固定产品入口：$BrokerFullPath"
}

$TempRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()
)
$SmokeDirectory = Join-Path `
    $TempRoot `
    "perf-monitor-broker-smoke-$([Guid]::NewGuid().ToString('N'))"
$SmokeDirectoryFull = [System.IO.Path]::GetFullPath(
    $SmokeDirectory
)
if (-not $SmokeDirectoryFull.StartsWith(
    $TempRoot,
    [StringComparison]::OrdinalIgnoreCase
)) {
    throw "Broker 冒烟目录超出系统临时目录。"
}

$PolicyPath = Join-Path `
    $SmokeDirectoryFull `
    "broker-policy-v1.json"
$DatabasePath = Join-Path $SmokeDirectoryFull "broker-v1.db"
$PipeName = "PerfMonitor.Broker.Smoke.$([Guid]::NewGuid().ToString('N'))"
$CurrentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$BrokerProcess = $null
$Pipe = $null

function Write-BrokerFrame {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]
        [object]$Payload
    )

    $Json = $Payload | ConvertTo-Json -Depth 12 -Compress
    $Encoding = [System.Text.UTF8Encoding]::new($false)
    $Bytes = $Encoding.GetBytes($Json)
    if ($Bytes.Length -le 0 -or $Bytes.Length -gt 262144) {
        throw "Broker 冒烟请求大小越界。"
    }

    $Header = [BitConverter]::GetBytes([uint32]$Bytes.Length)
    if (-not [BitConverter]::IsLittleEndian) {
        [Array]::Reverse($Header)
    }
    $Stream.Write($Header, 0, $Header.Length)
    $Stream.Write($Bytes, 0, $Bytes.Length)
    $Stream.Flush()
}

function Read-BrokerBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]
        [int]$Count
    )

    $Buffer = [byte[]]::new($Count)
    $Offset = 0
    while ($Offset -lt $Count) {
        $Read = $Stream.Read(
            $Buffer,
            $Offset,
            $Count - $Offset
        )
        if ($Read -le 0) {
            throw "Broker 在完整响应前关闭连接。"
        }
        $Offset += $Read
    }

    return ,$Buffer
}

function Read-BrokerFrame {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream
    )

    $Header = Read-BrokerBytes -Stream $Stream -Count 4
    if (-not [BitConverter]::IsLittleEndian) {
        [Array]::Reverse($Header)
    }
    $Length = [BitConverter]::ToUInt32($Header, 0)
    if ($Length -le 0 -or $Length -gt 262144) {
        throw "Broker 冒烟响应大小越界：$Length"
    }

    $Payload = Read-BrokerBytes `
        -Stream $Stream `
        -Count ([int]$Length)
    return (
        [System.Text.Encoding]::UTF8.GetString($Payload) |
        ConvertFrom-Json
    )
}

New-Item `
    -ItemType Directory `
    -Path $SmokeDirectoryFull |
    Out-Null
try {
    $Policy = [ordered]@{
        schemaVersion = 1
        policyVersion = "smoke-machine-v1"
        dryRunOnly = $false
        requireApprovedClientImage = $false
        requireTrustedClientSignature = $false
        requireProtectedClientPath = $false
        enabledActionTypes = @(
            "start_approved_diagnostic"
        )
        allowedCallerSids = @($CurrentSid)
        approvedClientImages = @()
        allowedPriorities = @()
        allowedDiagnosticIds = @(
            "broker.self_check"
        )
        allowedPowerProfileIds = @()
        deniedProcessNames = @(
            "perf-monitor-agent",
            "perf-monitor-broker",
            "perf-monitor-desktop"
        )
    }
    $Policy |
        ConvertTo-Json -Depth 8 |
        Set-Content `
            -LiteralPath $PolicyPath `
            -Encoding utf8

    $StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $StartInfo.FileName = $BrokerFullPath
    $StartInfo.UseShellExecute = $false
    $StartInfo.CreateNoWindow = $true
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    foreach ($ArgumentPath in @(
        $SmokeDirectoryFull,
        $PolicyPath
    )) {
        if ($ArgumentPath.Contains('"')) {
            throw "Broker 冒烟路径包含不可接受的引号。"
        }
    }
    $StartInfo.Arguments = (
        "--console " +
        "--data-directory `"$SmokeDirectoryFull`" " +
        "--policy `"$PolicyPath`" " +
        "--pipe-name $PipeName " +
        "--duration-seconds 8"
    )
    $BrokerProcess = [Diagnostics.Process]::Start($StartInfo)
    if ($null -eq $BrokerProcess) {
        throw "无法启动 Broker 冒烟进程。"
    }

    $ConnectDeadline = [DateTimeOffset]::UtcNow.AddSeconds(6)
    while ($null -eq $Pipe) {
        $Candidate = [System.IO.Pipes.NamedPipeClientStream]::new(
            ".",
            $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::None,
            [Security.Principal.TokenImpersonationLevel]::Impersonation
        )
        try {
            $Candidate.Connect(500)
            $Pipe = $Candidate
        }
        catch [TimeoutException] {
            $Candidate.Dispose()
            if ($BrokerProcess.HasExited) {
                $BrokerError =
                    $BrokerProcess.StandardError.ReadToEnd()
                throw (
                    "Broker 在 Pipe 就绪前退出：" +
                    "$($BrokerProcess.ExitCode) $BrokerError"
                )
            }
            if ([DateTimeOffset]::UtcNow -ge $ConnectDeadline) {
                throw "Broker Pipe 在 6 秒内未就绪。"
            }
        }
    }

    $HelloId = "smoke-hello"
    Write-BrokerFrame -Stream $Pipe -Payload ([ordered]@{
        type = "hello"
        requestId = $HelloId
        supportedBrokerProtocolVersions = @("1.0")
        maxMessageSize = 262144
    })
    $Hello = Read-BrokerFrame -Stream $Pipe
    if (
        $Hello.type -ne "helloAck" -or
        $Hello.requestId -ne $HelloId -or
        $Hello.selectedBrokerProtocolVersion -ne "1.0" -or
        -not $Hello.capabilities.brokerAvailable -or
        -not $Hello.capabilities.dryRunOnly
    ) {
        throw "Broker hello/capability 冒烟失败。"
    }

    $ActionId = "smoke-action"
    $IdempotencyKey = "smoke-diagnostic-once"
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $ActionRequest = [ordered]@{
        type = "executeAction"
        requestId = $ActionId
        idempotencyKey = $IdempotencyKey
        deadlineUtc = $Deadline.ToString("O")
        dryRun = $false
        agentPolicyVersion = "smoke-agent-v1"
        action = [ordered]@{
            actionType = "start_approved_diagnostic"
            diagnosticId = "broker.self_check"
        }
    }
    Write-BrokerFrame -Stream $Pipe -Payload $ActionRequest
    $First = Read-BrokerFrame -Stream $Pipe
    if (
        $First.type -ne "actionResult" -or
        $First.requestId -ne $ActionId -or
        $First.result.status -ne "dry_run" -or
        $First.result.idempotencyKey -ne $IdempotencyKey
    ) {
        throw "Broker 强制 dry-run 冒烟失败。"
    }

    $ReplayId = "smoke-replay"
    $ActionRequest.requestId = $ReplayId
    $ActionRequest.deadlineUtc = (
        [DateTimeOffset]::UtcNow.AddSeconds(10).ToString("O")
    )
    Write-BrokerFrame -Stream $Pipe -Payload $ActionRequest
    $Replay = Read-BrokerFrame -Stream $Pipe
    if (
        $Replay.type -ne "actionResult" -or
        $Replay.requestId -ne $ReplayId -or
        $Replay.result.status -ne "dry_run" -or
        $Replay.result.actionId -ne $First.result.actionId
    ) {
        throw "Broker 幂等重取冒烟失败。"
    }

    $Pipe.Dispose()
    $Pipe = $null
    if (-not $BrokerProcess.WaitForExit(15000)) {
        throw "Broker 没有在有界 duration 后退出。"
    }
    if ($BrokerProcess.ExitCode -ne 0) {
        $BrokerError = $BrokerProcess.StandardError.ReadToEnd()
        throw "Broker 冒烟退出码异常：$($BrokerProcess.ExitCode) $BrokerError"
    }
    if (
        -not (Test-Path -LiteralPath $DatabasePath) -or
        (Get-Item -LiteralPath $DatabasePath).Length -le 0
    ) {
        throw "Broker 没有生成独立审计数据库。"
    }

    Write-Output (
        "Broker 冒烟通过：transport identity、强制 dry-run、" +
        "幂等重取与独立审计数据库。"
    )
}
finally {
    if ($null -ne $Pipe) {
        $Pipe.Dispose()
    }
    if ($null -ne $BrokerProcess) {
        if (-not $BrokerProcess.HasExited) {
            $BrokerProcess.Kill()
            $BrokerProcess.WaitForExit()
        }
        $BrokerProcess.Dispose()
    }
    if (
        (Test-Path -LiteralPath $SmokeDirectoryFull) -and
        $SmokeDirectoryFull.StartsWith(
            $TempRoot,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        Remove-Item `
            -LiteralPath $SmokeDirectoryFull `
            -Recurse `
            -Force
    }
}
