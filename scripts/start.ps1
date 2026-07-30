[CmdletBinding()]
param(
    [switch]$DevHttp
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ($DevHttp) {
    $PythonPath = Join-Path `
        $ProjectRoot `
        ".venv\Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $PythonPath)) {
        throw "未找到 Python oracle 环境。请先运行 scripts\setup.ps1。"
    }

    Push-Location $ProjectRoot
    try {
        & $PythonPath app.py
        exit $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

$AgentPath = Join-Path `
    $ProjectRoot `
    "dist\agent\perf-monitor-agent.exe"
$DesktopPath = Join-Path `
    $ProjectRoot `
    "dist\desktop\perf-monitor-desktop.exe"
foreach ($RequiredPath in @($AgentPath, $DesktopPath)) {
    if (-not (Test-Path -LiteralPath $RequiredPath)) {
        throw (
            "未找到 .NET 产品产物：$RequiredPath`n" +
            "请先运行 scripts\build.ps1。"
        )
    }
}

$AgentProcess = Start-Process `
    -FilePath $AgentPath `
    -ArgumentList "--quiet" `
    -PassThru `
    -WindowStyle Hidden
try {
    $DesktopProcess = Start-Process `
        -FilePath $DesktopPath `
        -PassThru `
        -Wait
    exit $DesktopProcess.ExitCode
}
catch {
    $AgentProcess.Refresh()
    if (-not $AgentProcess.HasExited) {
        Stop-Process `
            -Id $AgentProcess.Id `
            -Force `
            -ErrorAction SilentlyContinue
    }
    throw
}
