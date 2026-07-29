[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VenvPath = Join-Path $ProjectRoot ".venv"
$VenvPython = Join-Path $VenvPath "Scripts\python.exe"
$Requirements = Join-Path $ProjectRoot "requirements-dev.txt"

function Assert-Python312 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PythonPath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $Version = (& $PythonPath -c `
        "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
    ).Trim()
    if ($LASTEXITCODE -ne 0 -or $Version -ne "3.12") {
        throw "$Description 必须是 Python 3.12，检测到：$Version"
    }
}

if (-not (Test-Path -LiteralPath $VenvPython)) {
    $SelectedPython = $null
    $PyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $PyLauncher) {
        $LauncherOutput = & $PyLauncher.Source -3.12 -c `
            "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $LauncherOutput) {
            $SelectedPython = $LauncherOutput.ToString().Trim()
        }
    }
    if (-not $SelectedPython) {
        $Python = Get-Command python -ErrorAction Stop
        $SelectedPython = $Python.Source
    }
    Assert-Python312 -PythonPath $SelectedPython `
        -Description "用于创建虚拟环境的解释器"
    & $SelectedPython -m venv $VenvPath
    if ($LASTEXITCODE -ne 0) {
        throw "创建 Python 虚拟环境失败。"
    }
}

Assert-Python312 -PythonPath $VenvPython -Description "项目虚拟环境"

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
