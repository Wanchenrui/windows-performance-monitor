[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$SpecFile = Join-Path $ProjectRoot "perf-monitor.spec"
$ExePath = Join-Path $ProjectRoot "dist\perf-monitor.exe"
$ManifestPath = Join-Path $ProjectRoot "dist\build-manifest.json"
$RuntimeLockPath = Join-Path $ProjectRoot "requirements.txt"
$DevelopmentLockPath = Join-Path $ProjectRoot "requirements-dev.txt"

if (-not (Test-Path -LiteralPath $VenvPython)) {
    throw "未找到 .venv。请先运行 scripts\setup.ps1。"
}

Push-Location $ProjectRoot
try {
    & $VenvPython -m pip check
    if ($LASTEXITCODE -ne 0) {
        throw "依赖一致性检查失败。"
    }

    & $VenvPython -m pytest
    if ($LASTEXITCODE -ne 0) {
        throw "测试失败，已阻止构建。"
    }

    & $VenvPython -m PyInstaller --noconfirm --clean $SpecFile
    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller 构建失败。"
    }

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "构建完成但未找到 perf-monitor.exe。"
    }

    $ExpectedVersion = (
        & $VenvPython -c "from perf_monitor import APP_VERSION; print(APP_VERSION)"
    ).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $ExpectedVersion) {
        throw "无法读取应用版本。"
    }

    $VersionInfo = (Get-Item -LiteralPath $ExePath).VersionInfo
    if (-not $VersionInfo.ProductVersion.StartsWith($ExpectedVersion)) {
        throw "产物版本资源不正确：$($VersionInfo.ProductVersion)"
    }

    $Hash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    $GitCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $GitCommit) {
        throw "无法读取 Git commit。"
    }
    $GitDirty = [bool](& git status --porcelain)
    $PyInstallerVersion = (
        & $VenvPython -m PyInstaller --version
    ).Trim()
    $PythonVersion = (& $VenvPython --version 2>&1).ToString().Trim()
    $PythonArchitecture = (
        & $VenvPython -c "import platform; print(platform.machine())"
    ).Trim()
    $BuildEnvironment = if ($env:GITHUB_ACTIONS -eq "true") {
        "github-actions"
    }
    else {
        "local"
    }
    $Manifest = [ordered]@{
        productVersion = $VersionInfo.ProductVersion
        fileVersion = $VersionInfo.FileVersion
        sha256 = $Hash
        sizeBytes = (Get-Item -LiteralPath $ExePath).Length
        builtAt = (Get-Date).ToUniversalTime().ToString("o")
        gitCommit = $GitCommit
        gitDirty = $GitDirty
        dependencyLockHashes = [ordered]@{
            requirements = (
                Get-FileHash -LiteralPath $RuntimeLockPath -Algorithm SHA256
            ).Hash
            requirementsDev = (
                Get-FileHash -LiteralPath $DevelopmentLockPath -Algorithm SHA256
            ).Hash
        }
        python = $PythonVersion
        pythonArchitecture = $PythonArchitecture
        pyInstaller = $PyInstallerVersion
        buildEnvironment = $BuildEnvironment
    }
    $Manifest |
        ConvertTo-Json |
        Set-Content -LiteralPath $ManifestPath -Encoding UTF8

    Write-Output "构建完成：$ExePath"
    Write-Output "SHA256：$Hash"
    Write-Output "校验清单：$ManifestPath"
}
finally {
    Pop-Location
}
