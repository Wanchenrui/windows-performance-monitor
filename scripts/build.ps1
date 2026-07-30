[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$SolutionPath = Join-Path $ProjectRoot "PerfMonitor.slnx"
$BuildPropertiesPath = Join-Path $ProjectRoot "Directory.Build.props"
$AgentProject = Join-Path `
    $ProjectRoot `
    "src\PerfMonitor.Agent\PerfMonitor.Agent.csproj"
$WorkerProject = Join-Path `
    $ProjectRoot `
    "src\PerfMonitor.ProviderWorker\PerfMonitor.ProviderWorker.csproj"
$DesktopProject = Join-Path `
    $ProjectRoot `
    "src\PerfMonitor.Desktop\PerfMonitor.Desktop.csproj"
$DistRoot = Join-Path $ProjectRoot "dist"
$AgentOutput = Join-Path $DistRoot "agent"
$WorkerOutput = Join-Path $AgentOutput "provider-worker"
$DesktopOutput = Join-Path $DistRoot "desktop"
$AgentExe = Join-Path $AgentOutput "perf-monitor-agent.exe"
$WorkerExe = Join-Path `
    $WorkerOutput `
    "perf-monitor-provider-worker.exe"
$DesktopExe = Join-Path $DesktopOutput "perf-monitor-desktop.exe"
$ManifestPath = Join-Path $DistRoot "build-manifest.json"
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"

if (-not (Test-Path -LiteralPath $VenvPython)) {
    throw "未找到 .venv。请先运行 scripts\setup.ps1。"
}
$null = Get-Command dotnet -ErrorAction Stop

$BuildProperties = [xml](
    Get-Content -LiteralPath $BuildPropertiesPath -Raw
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "Directory.Build.props 缺少产品版本。"
}
$ExpectedVersion = $VersionNode.InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    throw "Directory.Build.props 的产品版本为空。"
}

$DistRootFull = [System.IO.Path]::GetFullPath($DistRoot)
$DistPrefix = $DistRootFull.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar
) + [System.IO.Path]::DirectorySeparatorChar
foreach ($OutputPath in @($AgentOutput, $DesktopOutput)) {
    $OutputFull = [System.IO.Path]::GetFullPath($OutputPath)
    if (-not $OutputFull.StartsWith(
        $DistPrefix,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "拒绝清理 dist 之外的输出目录：$OutputFull"
    }
    if (Test-Path -LiteralPath $OutputFull) {
        Remove-Item -LiteralPath $OutputFull -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null

Push-Location $ProjectRoot
try {
    & $VenvPython -m pip check
    if ($LASTEXITCODE -ne 0) {
        throw "Python oracle 依赖一致性检查失败。"
    }

    & $VenvPython -m pytest
    if ($LASTEXITCODE -ne 0) {
        throw "Python oracle 测试失败，已阻止构建。"
    }

    & dotnet restore $SolutionPath --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw ".NET 锁定依赖还原失败。"
    }

    & dotnet test `
        $SolutionPath `
        --configuration Release `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw ".NET 测试失败，已阻止构建。"
    }

    & dotnet publish `
        $AgentProject `
        --configuration Release `
        --no-restore `
        --output $AgentOutput
    if ($LASTEXITCODE -ne 0) {
        throw ".NET Agent 发布失败。"
    }

    & dotnet publish `
        $WorkerProject `
        --configuration Release `
        --no-restore `
        --output $WorkerOutput
    if ($LASTEXITCODE -ne 0) {
        throw ".NET Provider Worker 发布失败。"
    }

    & dotnet publish `
        $DesktopProject `
        --configuration Release `
        --no-restore `
        --output $DesktopOutput
    if ($LASTEXITCODE -ne 0) {
        throw ".NET Desktop 发布失败。"
    }

    foreach ($RequiredPath in @(
        $AgentExe,
        $WorkerExe,
        $DesktopExe
    )) {
        if (-not (Test-Path -LiteralPath $RequiredPath)) {
            throw "构建完成但缺少产品入口：$RequiredPath"
        }
    }

    $AgentVersionInfo = (Get-Item -LiteralPath $AgentExe).VersionInfo
    $WorkerVersionInfo = (
        Get-Item -LiteralPath $WorkerExe
    ).VersionInfo
    $DesktopVersionInfo = (Get-Item -LiteralPath $DesktopExe).VersionInfo
    foreach ($VersionInfo in @(
        $AgentVersionInfo,
        $WorkerVersionInfo,
        $DesktopVersionInfo
    )) {
        if (
            [string]::IsNullOrWhiteSpace($VersionInfo.ProductVersion) -or
            -not $VersionInfo.ProductVersion.StartsWith(
                $ExpectedVersion,
                [StringComparison]::Ordinal
            )
        ) {
            throw "产品版本资源不正确：$($VersionInfo.ProductVersion)"
        }
    }

    $ProductFiles = @(
        Get-ChildItem `
            -LiteralPath $AgentOutput, $DesktopOutput `
            -File `
            -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $RelativePath = $_.FullName.Substring(
                $DistRootFull.Length + 1
            ).Replace("\", "/")
            [ordered]@{
                path = $RelativePath
                sha256 = (
                    Get-FileHash `
                        -LiteralPath $_.FullName `
                        -Algorithm SHA256
                ).Hash
                sizeBytes = $_.Length
            }
        }
    )

    $DependencyPaths =
        [System.Collections.Generic.List[string]]::new()
    foreach ($Path in @(
        (Join-Path $ProjectRoot "requirements.txt"),
        (Join-Path $ProjectRoot "requirements-dev.txt"),
        (Join-Path $ProjectRoot "global.json"),
        $BuildPropertiesPath
    )) {
        $null = $DependencyPaths.Add($Path)
    }
    foreach ($LockFile in Get-ChildItem `
        -LiteralPath (
            Join-Path $ProjectRoot "src"
        ), (
            Join-Path $ProjectRoot "tests"
        ) `
        -Filter "packages.lock.json" `
        -File `
        -Recurse) {
        $null = $DependencyPaths.Add($LockFile.FullName)
    }

    $DependencyLockHashes = [ordered]@{}
    foreach ($LockPath in $DependencyPaths |
        Sort-Object -Unique) {
        $LockFull = [System.IO.Path]::GetFullPath($LockPath)
        $ProjectPrefix = $ProjectRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar
        ) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $LockFull.StartsWith(
            $ProjectPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "依赖锁文件位于项目目录之外：$LockFull"
        }
        $RelativeLockPath = $LockFull.Substring(
            $ProjectRoot.Length + 1
        ).Replace("\", "/")
        $DependencyLockHashes[$RelativeLockPath] = (
            Get-FileHash -LiteralPath $LockFull -Algorithm SHA256
        ).Hash
    }

    $GitCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $GitCommit) {
        throw "无法读取 Git commit。"
    }
    $GitDirty = [bool](& git status --porcelain)
    $PythonVersion = (
        & $VenvPython --version 2>&1
    ).ToString().Trim()
    $DotnetSdkVersion = (& dotnet --version).Trim()
    $BuildEnvironment = if ($env:GITHUB_ACTIONS -eq "true") {
        "github-actions"
    }
    else {
        "local"
    }

    $Manifest = [ordered]@{
        productVersion = $ExpectedVersion
        contractVersion = "1.0"
        deployment = "framework-dependent"
        targetFramework = "net10.0-windows10.0.17763.0"
        runtimeIdentifier = "win-x64"
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        gitCommit = $GitCommit
        gitDirty = $GitDirty
        files = $ProductFiles
        dependencyLockHashes = $DependencyLockHashes
        toolchain = [ordered]@{
            dotnetSdk = $DotnetSdkVersion
            pythonOracle = $PythonVersion
        }
        buildEnvironment = $BuildEnvironment
    }
    $Manifest |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $ManifestPath -Encoding UTF8

    Write-Output "Agent 构建完成：$AgentExe"
    Write-Output "Provider Worker 构建完成：$WorkerExe"
    Write-Output "Desktop 构建完成：$DesktopExe"
    Write-Output "校验清单：$ManifestPath"
}
finally {
    Pop-Location
}
