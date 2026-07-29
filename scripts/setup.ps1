[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VenvPath = Join-Path $ProjectRoot ".venv"
$VenvPython = Join-Path $VenvPath "Scripts\python.exe"
$Requirements = Join-Path $ProjectRoot "requirements-dev.txt"

if (-not (Test-Path -LiteralPath $VenvPython)) {
    $PyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $PyLauncher) {
        & $PyLauncher.Source -3.12 -m venv $VenvPath
    }
    else {
        $Python = Get-Command python -ErrorAction Stop
        & $Python.Source -m venv $VenvPath
    }
    if ($LASTEXITCODE -ne 0) {
        throw "创建 Python 虚拟环境失败。"
    }
}

& $VenvPython -m pip install --disable-pip-version-check -r $Requirements
if ($LASTEXITCODE -ne 0) {
    throw "安装锁定依赖失败。"
}

& $VenvPython -m pip check
if ($LASTEXITCODE -ne 0) {
    throw "依赖一致性检查失败。"
}

Write-Output "环境已就绪：$VenvPython"
Write-Output "运行方式：.\启动.bat"

