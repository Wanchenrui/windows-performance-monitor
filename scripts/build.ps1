[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$SpecFile = Join-Path $ProjectRoot "perf-monitor.spec"
$ExePath = Join-Path $ProjectRoot "dist\perf-monitor.exe"
$ManifestPath = Join-Path $ProjectRoot "dist\build-manifest.json"

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

    $VersionInfo = (Get-Item -LiteralPath $ExePath).VersionInfo
    if (-not $VersionInfo.ProductVersion.StartsWith("0.2.0")) {
        throw "产物版本资源不正确：$($VersionInfo.ProductVersion)"
    }

    $Hash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    $Manifest = [ordered]@{
        productVersion = $VersionInfo.ProductVersion
        fileVersion = $VersionInfo.FileVersion
        sha256 = $Hash
        sizeBytes = (Get-Item -LiteralPath $ExePath).Length
        builtAt = (Get-Date).ToUniversalTime().ToString("o")
        python = (& $VenvPython --version 2>&1).ToString()
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

