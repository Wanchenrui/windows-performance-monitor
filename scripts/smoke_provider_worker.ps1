[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkerPath,

    [string]$DotnetHost = "",

    [ValidateRange(1000, 30000)]
    [int]$TimeoutMilliseconds = 10000
)

$ErrorActionPreference = "Stop"
$WorkerPath = (Resolve-Path -LiteralPath $WorkerPath).Path
if (
    -not [string]::Equals(
        [System.IO.Path]::GetFileName($WorkerPath),
        "perf-monitor-provider-worker.exe",
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "Worker 路径不是固定的产品入口。"
}

function Read-Exact {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory = $true)]
        [byte[]]$Buffer,

        [int]$Offset,

        [int]$Count,

        [int]$Timeout
    )

    $ReadTotal = 0
    while ($ReadTotal -lt $Count) {
        $ReadTask = $Stream.ReadAsync(
            $Buffer,
            $Offset + $ReadTotal,
            $Count - $ReadTotal
        )
        if (-not $ReadTask.Wait($Timeout)) {
            throw "Worker 协议读取超时。"
        }
        $Read = $ReadTask.Result
        if ($Read -le 0) {
            throw "Worker 在完整响应前关闭了标准输出。"
        }
        $ReadTotal += $Read
    }
}

$StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
$StartInfo.FileName = $WorkerPath
$StartInfo.Arguments = "--stdio"
if ($DotnetHost) {
    $DotnetHost = (Resolve-Path -LiteralPath $DotnetHost).Path
    $WorkerDll = [System.IO.Path]::ChangeExtension(
        $WorkerPath,
        ".dll"
    )
    if (-not (Test-Path -LiteralPath $WorkerDll)) {
        throw "使用 dotnet host 时缺少 Worker DLL。"
    }
    $StartInfo.FileName = $DotnetHost
    $StartInfo.Arguments = "`"$WorkerDll`" --stdio"
}
$StartInfo.WorkingDirectory = Split-Path -Parent $WorkerPath
$StartInfo.UseShellExecute = $false
$StartInfo.CreateNoWindow = $true
$StartInfo.RedirectStandardInput = $true
$StartInfo.RedirectStandardOutput = $true
$StartInfo.RedirectStandardError = $true
$NoBomEncoding = [System.Text.UTF8Encoding]::new($false)
$InputEncodingProperty = $StartInfo.PSObject.Properties[
    "StandardInputEncoding"
]
if ($null -eq $InputEncodingProperty) {
    throw (
        "Worker 二进制协议冒烟需要 PowerShell 7，" +
        "Windows PowerShell 5.1 会向标准输入写入 UTF-8 BOM。"
    )
}
$StartInfo.StandardInputEncoding = $NoBomEncoding
$StartInfo.StandardOutputEncoding = $NoBomEncoding
$StartInfo.StandardErrorEncoding = $NoBomEncoding
$Process = [System.Diagnostics.Process]::new()
$Process.StartInfo = $StartInfo
$ProcessStarted = $false

try {
    if (-not $Process.Start()) {
        throw "Worker 进程未启动。"
    }
    $ProcessStarted = $true
    $StandardInputWriter = $Process.StandardInput

    $RequestId = [Guid]::NewGuid().ToString("N")
    $Request = [ordered]@{
        protocolVersion = "1.0"
        requestId = $RequestId
        operation = "collect"
    } | ConvertTo-Json -Compress
    $Encoding = [System.Text.UTF8Encoding]::new($false)
    $Payload = $Encoding.GetBytes($Request)
    $Prefix = [BitConverter]::GetBytes([int]$Payload.Length)
    $InputStream = $StandardInputWriter.BaseStream
    $InputStream.Write($Prefix, 0, $Prefix.Length)
    $InputStream.Write($Payload, 0, $Payload.Length)
    $InputStream.Flush()

    $ResponsePrefix = [byte[]]::new(4)
    Read-Exact `
        -Stream $Process.StandardOutput.BaseStream `
        -Buffer $ResponsePrefix `
        -Offset 0 `
        -Count 4 `
        -Timeout $TimeoutMilliseconds
    $ResponseLength = [BitConverter]::ToInt32(
        $ResponsePrefix,
        0
    )
    if ($ResponseLength -le 0 -or $ResponseLength -gt 1048576) {
        throw "Worker 响应长度越界：$ResponseLength"
    }

    $ResponsePayload = [byte[]]::new($ResponseLength)
    Read-Exact `
        -Stream $Process.StandardOutput.BaseStream `
        -Buffer $ResponsePayload `
        -Offset 0 `
        -Count $ResponseLength `
        -Timeout $TimeoutMilliseconds
    $Response = $Encoding.GetString(
        $ResponsePayload
    ) | ConvertFrom-Json
    if ($Response.protocolVersion -ne "1.0") {
        throw "Worker 协议版本错误。"
    }
    if ($Response.requestId -ne $RequestId) {
        throw "Worker 未回显 requestId。"
    }
    if (
        -not $Response.workerInstanceId -or
        $Response.workerInstanceId -notmatch "^[0-9a-f]{32}$"
    ) {
        throw "Worker instanceId 无效。"
    }
    if ([Int64]$Response.sequence -ne 1) {
        throw "Worker 首个 sequence 必须为 1。"
    }
    if (
        @(
            "available",
            "partial",
            "not_supported",
            "permission_denied",
            "error"
        ) -notcontains $Response.status
    ) {
        throw "Worker 状态无效：$($Response.status)"
    }
    if ($null -eq $Response.coverage) {
        throw "Worker 响应缺少 coverage。"
    }

    $StandardInputWriter.Close()
    if (-not $Process.WaitForExit(5000)) {
        throw "Worker 未在标准输入关闭后退出。"
    }
    if ($Process.ExitCode -ne 0) {
        throw "Worker 冒烟退出码错误：$($Process.ExitCode)"
    }

    Write-Output (
        "Worker 冒烟通过：instanceId={0} sequence={1} " +
        "status={2} devices={3}" -f `
            $Response.workerInstanceId,
            $Response.sequence,
            $Response.status,
            @($Response.devices).Count
    )
}
catch {
    $Failure = $_.Exception
    if ($ProcessStarted -and $Process.HasExited) {
        $WorkerError = $Process.StandardError.ReadToEnd().Trim()
        throw [InvalidOperationException]::new(
            (
                "{0} WorkerExitCode={1} WorkerStderr={2}" -f `
                    $Failure.Message,
                    $Process.ExitCode,
                    $WorkerError
            ),
            $Failure
        )
    }
    throw
}
finally {
    if (
        $ProcessStarted -and
        -not $Process.HasExited
    ) {
        $Process.Kill()
        $Process.WaitForExit()
    }
    $Process.Dispose()
}
