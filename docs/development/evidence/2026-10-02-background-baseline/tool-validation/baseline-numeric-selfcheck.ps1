param([Parameter(Mandatory)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
$repo = 'D:\GitHub\windows-performance-monitor'
$scriptPath = Join-Path $repo 'scripts/run_product_baseline.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Script AST parse failed' }
function Get-Assignment([string]$Name) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq ('$' + $Name) }, $true)
    if (!$node) { throw "Assignment missing: $Name" }
    $node.Extent.Text
}
function Get-FieldExpression([string]$Name) {
    foreach ($table in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.HashtableAst] }, $true)) {
        foreach ($pair in $table.KeyValuePairs) {
            if ($pair.Item1.Value -eq $Name) { return $pair.Item2.Extent.Text }
        }
    }
    throw "Field missing: $Name"
}
function Assert-Near([double]$Actual, [double]$Expected, [string]$Name) {
    if ([Math]::Abs($Actual - $Expected) -gt 0.000000001) { throw "$Name expected $Expected got $Actual" }
}
$deltaCode = Get-Assignment 'Delta'
$Child = [pscustomobject]@{ LastCpu = 1.0 }
$Cpu = 1.03125; Invoke-Expression $deltaCode; Assert-Near $Delta 0.03125 'fractional delta'
$Cpu = 1.75; Invoke-Expression $deltaCode; Assert-Near $Delta 0.75 'fractional delta 0.75'
$Cpu = 0.5; Invoke-Expression $deltaCode; Assert-Near $Delta 0 'negative clamp'
$Child.LastCpu = $null; Invoke-Expression $deltaCode
if ($null -ne $Delta) { throw 'First CPU interval must stay unknown' }
$Child = [pscustomobject]@{ LastCpu = 1.0; Record = [ordered]@{} }
$CpuComplete = $true
$sampleCatch = $ast.Find({ param($n) $n -is [Management.Automation.Language.CatchClauseAst] -and $n.Body.Extent.Text -match 'sampleError' }, $true)
try { throw 'Synthetic failed counter read' } catch { Invoke-Expression ($sampleCatch.Body.Statements.Extent.Text -join "`n") }
if ($null -ne $Child.LastCpu -or $CpuComplete -or !$Child.Record.sampleError) { throw 'Failed sample retained CPU baseline' }
$Cpu = 1.5; Invoke-Expression $deltaCode
if ($null -ne $Delta) { throw 'First sample after failure must not charge a multi-probe CPU delta' }
$Child.LastCpu = $Cpu; $Cpu = 1.53125; Invoke-Expression $deltaCode
Assert-Near $Delta 0.03125 'CPU resumes after baseline reset'
$Evidence = [ordered]@{ workerDiscoveryErrors = 0 }; $DiscoveryComplete = $true
$discoveryCatch = $ast.Find({ param($n) $n -is [Management.Automation.Language.CatchClauseAst] -and $n.Body.Extent.Text -match 'workerDiscoveryErrors' }, $true)
try { throw 'Synthetic worker identity mismatch' } catch { Invoke-Expression ($discoveryCatch.Body.Statements.Extent.Text -join "`n") }
if ($DiscoveryComplete -or $Evidence.workerDiscoveryErrors -ne 1) { throw 'Worker discovery failure did not mark the probe incomplete' }
$Interval = 1.0; Invoke-Expression (Get-Assignment 'CpuComplete')
if ($CpuComplete) { throw 'Failed discovery treated as a complete CPU interval' }
$DiscoveryComplete = $true; Invoke-Expression (Get-Assignment 'CpuComplete')
if (!$CpuComplete) { throw 'Successful discovery did not restore interval eligibility' }
$WarmupSeconds = 10; $DurationSeconds = 10
$Computer = [pscustomobject]@{ NumberOfLogicalProcessors = 4 }
$SetRows = @(
    [pscustomobject]@{ phase = 'startup-warmup'; intervalStartSeconds = 0; intervalSeconds = 9; cpuCoverageComplete = $true; deltaCpuSeconds = 100 },
    [pscustomobject]@{ phase = 'measurement'; intervalStartSeconds = 9; intervalSeconds = 2; cpuCoverageComplete = $true; deltaCpuSeconds = 999 },
    [pscustomobject]@{ phase = 'measurement'; intervalStartSeconds = 11; intervalSeconds = 1; cpuCoverageComplete = $true; deltaCpuSeconds = 0.03125 },
    [pscustomobject]@{ phase = 'measurement'; intervalStartSeconds = 12; intervalSeconds = 3; cpuCoverageComplete = $true; deltaCpuSeconds = 0.75 },
    [pscustomobject]@{ phase = 'measurement'; intervalStartSeconds = 15; intervalSeconds = 1; cpuCoverageComplete = $false; deltaCpuSeconds = 123 }
)
Invoke-Expression (Get-Assignment 'CompleteRows')
Invoke-Expression (Get-Assignment 'CpuSeconds')
Invoke-Expression (Get-Assignment 'CoveredSeconds')
if ($CompleteRows.Count -ne 2) { throw 'Warmup boundary or incomplete CPU entered mean' }
Assert-Near $CpuSeconds 0.78125 'CPU sum'
Assert-Near $CoveredSeconds 4 'covered duration'
Assert-Near (Invoke-Expression (Get-FieldExpression 'cpuCoreEquivalentMeanPct')) 19.53125 'time weighted mean'
Assert-Near (Invoke-Expression (Get-FieldExpression 'cpuMachineNormalizedMeanPct')) 4.8828125 'machine normalization'
$CpuDelta = 0.03125 + 0.75; $Interval = 4.0; $CpuComplete = $true
Assert-Near (Invoke-Expression (Get-FieldExpression 'cpuCoreEquivalentPct')) 19.53125 'process set rate'
Assert-Near (Invoke-Expression (Get-FieldExpression 'cpuMachineNormalizedPct')) 4.8828125 'process set normalized rate'
$SampleElapsed = 1.03125; $NextProbe = 1.0
Assert-Near (Invoke-Expression (Get-FieldExpression 'probeLatenessSeconds')) 0.03125 'fractional lateness'
$oldRoot = Join-Path $repo 'artifacts/baseline/2026-10-02-wave02-agent-toolcheck-60s'
$replay = @()
foreach ($group in (Import-Csv -LiteralPath (Join-Path $oldRoot 'process-resources.csv') | Group-Object kind)) {
    $rows = @($group.Group | Sort-Object { [double]$_.elapsedSeconds })
    $Child = [pscustomobject]@{ LastCpu = $null }; $sum = 0.0
    foreach ($row in $rows) {
        $Cpu = [double]$row.totalCpuSeconds; Invoke-Expression $deltaCode
        if ($null -ne $Delta) { $sum += $Delta }
        $Child.LastCpu = $Cpu
    }
    $expected = [double]$rows[-1].totalCpuSeconds - [double]$rows[0].totalCpuSeconds
    Assert-Near $sum $expected ($group.Name + ' cumulative CPU telescoping')
    if ($sum -le 0) { throw 'Old CSV must reproduce nonzero CPU' }
    $replay += [ordered]@{ kind = $group.Name; rows = $rows.Count; cumulativeDifferenceSeconds = $expected; correctedDeltaSumSeconds = $sum; boundary = 'offline numeric regression only; not a replacement measurement' }
}
$storageNode = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-StorageSizes' }, $true)
Invoke-Expression $storageNode.Extent.Text
$Data = Join-Path $PSScriptRoot ('numeric-storage-fixture-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $Data
$empty = Get-StorageSizes
if ($empty.Count -ne 3 -or $empty.dbBytes -ne 0 -or $empty.walBytes -ne 0 -or $empty.shmBytes -ne 0) { throw 'Absent storage mismatch' }
[IO.File]::WriteAllBytes((Join-Path $Data 'history-v1.db'), [byte[]]::new(17))
$dbOnly = Get-StorageSizes
[IO.File]::WriteAllBytes((Join-Path $Data 'history-v1.db-wal'), [byte[]]::new(31))
[IO.File]::WriteAllBytes((Join-Path $Data 'history-v1.db-shm'), [byte[]]::new(7))
$all = Get-StorageSizes
if ($dbOnly.dbBytes -ne 17 -or $dbOnly.walBytes -ne 0 -or $all.dbBytes -ne 17 -or $all.walBytes -ne 31 -or $all.shmBytes -ne 7) { throw 'Storage sizes mismatch' }
$roundtrip = [ordered]@{storageBeforeShutdown=$all;storageAtFirstMeasurementProbe=$dbOnly;storageAfterShutdown=$empty} | ConvertTo-Json -Depth 5 | ConvertFrom-Json
if ($roundtrip.storageBeforeShutdown.walBytes -ne 31) { throw 'Default JSON parser roundtrip failed' }
$oldRejected = $false
try { $null = Get-Content -LiteralPath (Join-Path $oldRoot 'baseline.json') -Raw | ConvertFrom-Json } catch { $oldRejected = $true }
if (!$oldRejected) { throw 'Original empty-key reproduction missing' }
[ordered]@{status='passed';powershell=$PSVersionTable.PSVersion.ToString();scriptSha256=(Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash;originalEvidenceSha256=(Get-FileHash -LiteralPath (Join-Path $oldRoot 'baseline.json') -Algorithm SHA256).Hash;checks=@('fractional CPU','negative clamp','unknown first interval','failed sample resets baseline','first recovery sample is unknown','next recovery delta is valid','worker discovery failure excludes CPU interval','process-set sum','time-weighted mean','machine normalization','warmup crossing excluded','incomplete interval excluded','fractional probe lateness','real CSV cumulative difference replay','storage sizes','default ConvertFrom-Json compatibility','original invalid JSON reproduced');offlineReplay=$replay;productStarted=$false;physicalInputUsed=$false} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $ResultPath -Encoding utf8
Get-Content -LiteralPath $ResultPath
