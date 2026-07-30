[CmdletBinding()]
param(
    [string]$AgentPath = "",

    [string]$SupportPath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")
$BuildProperties = [xml](
    Get-Content -LiteralPath (
        Join-Path $ProjectRoot "Directory.Build.props"
    ) -Raw
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "crash_recovery_product_version_missing"
}
$ExpectedProductVersion = $VersionNode.InnerText.Trim()
if (-not $AgentPath) {
    $AgentPath = Join-Path `
        $ProjectRoot `
        "dist\agent\perf-monitor-agent.exe"
}
if (-not $SupportPath) {
    $SupportPath = Join-Path `
        $ProjectRoot `
        "dist\support\perf-monitor-support.exe"
}
$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
$SupportPath = (Resolve-Path -LiteralPath $SupportPath).Path
$AgentSha256 = Get-PerfMonitorSha256 -Path $AgentPath
$SupportSha256 = Get-PerfMonitorSha256 -Path $SupportPath
$TempRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()
)
$DataRoot = Join-Path `
    $TempRoot `
    "perf-monitor-crash-$([Guid]::NewGuid().ToString('N'))"
$DatabasePath = Join-Path $DataRoot "history-v1.db"
$BeforeOutput = Join-Path $DataRoot "before-crash.jsonl"
$AfterOutput = Join-Path $DataRoot "after-restart.jsonl"
$DiagnosticBefore = Join-Path $DataRoot "before.zip"
$DiagnosticAfter = Join-Path $DataRoot "after.zip"
$AgentProcess = $null

function Get-DatabaseSnapshotCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $Archive = [IO.Compression.ZipFile]::OpenRead($OutputPath)
    try {
        $Entry = $Archive.GetEntry("diagnostics.json")
        if ($null -eq $Entry) {
            throw "crash_recovery_diagnostic_entry_missing"
        }
        $Reader = [IO.StreamReader]::new($Entry.Open())
        try {
            $Document = $Reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $Reader.Dispose()
        }
        return [Int64]$Document.database.snapshotCount
    }
    finally {
        $Archive.Dispose()
    }
}

function Assert-DatabaseIntegrity {
    $Verification = (
        & $SupportPath database verify `
            --path $DatabasePath
    ) | Select-Object -Last 1 | ConvertFrom-Json
    if (
        $LASTEXITCODE -ne 0 -or
        -not $Verification.integrityPassed -or
        [int]$Verification.schemaVersion -ne 2
    ) {
        throw "crash_recovery_integrity_failed"
    }
}

$null = New-Item -ItemType Directory -Path $DataRoot
try {
    $Arguments = @(
        "--quiet",
        "--warmup-seconds",
        "0",
        "--output-period-ms",
        "100",
        "--data-directory",
        "`"$DataRoot`"",
        "--output",
        "`"$BeforeOutput`""
    )
    $AgentProcess = Start-Process `
        -FilePath $AgentPath `
        -ArgumentList $Arguments `
        -PassThru `
        -WindowStyle Hidden

    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    $Ready = $false
    while ([DateTimeOffset]::UtcNow -lt $Deadline) {
        $AgentProcess.Refresh()
        if ($AgentProcess.HasExited) {
            throw (
                "crash_recovery_agent_exited_early:" +
                $AgentProcess.ExitCode
            )
        }
        $LineCount = if (
            Test-Path -LiteralPath $BeforeOutput
        ) {
            @(
                Get-Content -LiteralPath $BeforeOutput
            ).Count
        }
        else {
            0
        }
        if (
            $LineCount -ge 5 -and
            (Test-Path -LiteralPath $DatabasePath) -and
            (Test-Path -LiteralPath ($DatabasePath + "-wal"))
        ) {
            $Ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $Ready) {
        throw "crash_recovery_wal_write_not_observed"
    }

    Stop-Process -Id $AgentProcess.Id -Force
    $AgentProcess.WaitForExit()
    $AgentProcess.Dispose()
    $AgentProcess = $null

    Assert-DatabaseIntegrity
    & $SupportPath diagnostics export `
        --data-directory $DataRoot `
        --output $DiagnosticBefore
    if ($LASTEXITCODE -ne 0) {
        throw "crash_recovery_pre_restart_export_failed"
    }
    $BeforeCount = Get-DatabaseSnapshotCount `
        -OutputPath $DiagnosticBefore
    if ($BeforeCount -le 0) {
        throw "crash_recovery_committed_rows_missing"
    }

    & $AgentPath `
        --quiet `
        --warmup-seconds 0 `
        --duration-seconds 5 `
        --output-period-ms 100 `
        --data-directory $DataRoot `
        --output $AfterOutput
    if ($LASTEXITCODE -ne 0) {
        throw "crash_recovery_restart_failed"
    }
    Assert-DatabaseIntegrity
    & $SupportPath diagnostics export `
        --data-directory $DataRoot `
        --output $DiagnosticAfter
    if ($LASTEXITCODE -ne 0) {
        throw "crash_recovery_post_restart_export_failed"
    }
    $AfterCount = Get-DatabaseSnapshotCount `
        -OutputPath $DiagnosticAfter
    if ($AfterCount -le $BeforeCount) {
        throw "crash_recovery_history_did_not_continue"
    }

    $BeforeSnapshot = Get-Content `
        -LiteralPath $BeforeOutput |
        Select-Object -Last 1 |
        ConvertFrom-Json
    $AfterSnapshot = Get-Content `
        -LiteralPath $AfterOutput |
        Select-Object -Last 1 |
        ConvertFrom-Json
    if (
        $BeforeSnapshot.contractVersion -cne "1.0" -or
        $AfterSnapshot.contractVersion -cne "1.0" -or
        $BeforeSnapshot.productVersion -cne
            $ExpectedProductVersion -or
        $AfterSnapshot.productVersion -cne
            $ExpectedProductVersion -or
        -not $BeforeSnapshot.instanceId -or
        -not $AfterSnapshot.instanceId -or
        $BeforeSnapshot.instanceId -ceq
            $AfterSnapshot.instanceId
    ) {
        throw "crash_recovery_instance_restart_not_detected"
    }

    [ordered]@{
        schemaVersion = "1.0"
        productVersion = $ExpectedProductVersion
        databaseSchemaVersion = 2
        agentSha256 = $AgentSha256
        supportSha256 = $SupportSha256
        passed = $true
        walObservedBeforeTermination = $true
        integrityPassedAfterTermination = $true
        integrityPassedAfterRestart = $true
        committedSnapshotCountBeforeRestart = $BeforeCount
        snapshotCountAfterRestart = $AfterCount
        instanceChanged = $true
    } | ConvertTo-Json
}
finally {
    if (
        $null -ne $AgentProcess -and
        -not $AgentProcess.HasExited
    ) {
        Stop-Process `
            -Id $AgentProcess.Id `
            -Force `
            -ErrorAction SilentlyContinue
    }
    if ($null -ne $AgentProcess) {
        $AgentProcess.Dispose()
    }
    $DataRootFull = [System.IO.Path]::GetFullPath($DataRoot)
    $TempPrefix = $TempRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if (
        $DataRootFull.StartsWith(
            $TempPrefix,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        (Split-Path -Leaf $DataRootFull).StartsWith(
            "perf-monitor-crash-",
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
