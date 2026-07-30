[CmdletBinding()]
param(
    [string]$OutputDirectory = "",

    [string]$AuditPythonPath = "",

    [switch]$BootstrapAuditTool,

    [switch]$ReleaseScope,

    [string]$WaiverPath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path `
        $ProjectRoot `
        "artifacts\vulnerability"
}
$OutputDirectory = [System.IO.Path]::GetFullPath(
    $OutputDirectory
)
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force
if (-not $WaiverPath) {
    $WaiverPath = Join-Path `
        $ProjectRoot `
        "release\vulnerability-waivers-v1.json"
}
$WaiverPath = (Resolve-Path -LiteralPath $WaiverPath).Path

if (-not $AuditPythonPath) {
    $AuditPythonPath = Join-Path `
        $ProjectRoot `
        ".tools\pip-audit-2.10.1\Scripts\python.exe"
}
$AuditPythonPath = [System.IO.Path]::GetFullPath(
    $AuditPythonPath
)
if (
    $BootstrapAuditTool -and
    -not (Test-Path -LiteralPath $AuditPythonPath)
) {
    $EnvironmentRoot = Split-Path `
        -Parent `
        (Split-Path -Parent $AuditPythonPath)
    $EnvironmentParent = Split-Path -Parent $EnvironmentRoot
    $ProjectToolsRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $ProjectRoot ".tools")
    )
    $ProjectToolsPrefix = $ProjectToolsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $EnvironmentRoot.StartsWith(
        $ProjectToolsPrefix,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "pip_audit_bootstrap_path_not_allowed"
    }
    $BasePython = Join-Path `
        $ProjectRoot `
        ".venv\Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $BasePython)) {
        throw "pip_audit_base_python_missing"
    }
    $null = New-Item `
        -ItemType Directory `
        -Path $EnvironmentParent `
        -Force
    & $BasePython -m venv $EnvironmentRoot
    if ($LASTEXITCODE -ne 0) {
        throw "pip_audit_venv_create_failed"
    }
    & $AuditPythonPath -m pip install `
        --disable-pip-version-check `
        --require-hashes `
        --only-binary=:all: `
        -r (Join-Path $ProjectRoot "requirements-audit.txt")
    if ($LASTEXITCODE -ne 0) {
        throw "pip_audit_install_failed"
    }
}
if (-not (Test-Path -LiteralPath $AuditPythonPath)) {
    throw (
        "pip_audit_missing: run with -BootstrapAuditTool " +
        "after creating the verified Python 3.12 environment"
    )
}

$AuditVersion = (
    & $AuditPythonPath -m pip_audit --version 2>&1
).ToString().Trim()
if (
    $LASTEXITCODE -ne 0 -or
    $AuditVersion -notmatch "(^| )2\.10\.1($| )"
) {
    throw "pip_audit_version_mismatch:$AuditVersion"
}

$Findings = [System.Collections.Generic.List[object]]::new()
function Add-VulnerabilityFindings {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Node,

        [Parameter(Mandatory = $true)]
        [string]$Ecosystem,

        [string]$PackageName = ""
    )

    if ($null -eq $Node -or $Node -is [string]) {
        return
    }
    if (
        $Node -is [System.Collections.IEnumerable] -and
        $Node -isnot [pscustomobject]
    ) {
        foreach ($Item in $Node) {
            if ($null -ne $Item) {
                Add-VulnerabilityFindings `
                    -Node $Item `
                    -Ecosystem $Ecosystem `
                    -PackageName $PackageName
            }
        }
        return
    }

    $Properties = @($Node.PSObject.Properties)
    $LocalPackage = $PackageName
    $NameProperty = $Properties |
        Where-Object {
            $_.Name -in @("name", "id")
        } |
        Select-Object -First 1
    if (
        $null -ne $NameProperty -and
        $NameProperty.Value -is [string]
    ) {
        $LocalPackage = [string]$NameProperty.Value
    }

    foreach ($CollectionName in @(
        "vulnerabilities",
        "vulns"
    )) {
        $VulnerabilityProperty = $Properties |
            Where-Object { $_.Name -ceq $CollectionName } |
            Select-Object -First 1
        if ($null -eq $VulnerabilityProperty) {
            continue
        }
        foreach ($Vulnerability in @(
            $VulnerabilityProperty.Value
        )) {
            $VulnerabilityProperties = @(
                $Vulnerability.PSObject.Properties
            )
            $AdvisoryProperty = $VulnerabilityProperties |
                Where-Object {
                    $_.Name -in @(
                        "id",
                        "advisoryurl",
                        "advisoryUrl"
                    ) -and
                    -not [string]::IsNullOrWhiteSpace(
                        [string]$_.Value
                    )
                } |
                Select-Object -First 1
            $Advisory = if ($null -ne $AdvisoryProperty) {
                [string]$AdvisoryProperty.Value
            }
            else {
                ""
            }
            if (-not $Advisory) {
                throw "vulnerability_advisory_id_missing"
            }
            if ([string]::IsNullOrWhiteSpace($LocalPackage)) {
                throw "vulnerability_package_missing:$Advisory"
            }
            $Aliases = @()
            $AliasesProperty = $VulnerabilityProperties |
                Where-Object { $_.Name -ceq "aliases" } |
                Select-Object -First 1
            if ($null -ne $AliasesProperty) {
                $Aliases = @($AliasesProperty.Value)
            }
            $SeverityProperty = $VulnerabilityProperties |
                Where-Object { $_.Name -ceq "severity" } |
                Select-Object -First 1
            $Findings.Add(
                [pscustomobject]@{
                    ecosystem = $Ecosystem
                    package = $LocalPackage
                    advisoryId = $Advisory
                    aliases = $Aliases
                    severity = if (
                        $null -ne $SeverityProperty -and
                        -not [string]::IsNullOrWhiteSpace(
                            [string]$SeverityProperty.Value
                        )
                    ) {
                        [string]$SeverityProperty.Value
                    }
                    else {
                        $null
                    }
                }
            )
        }
    }

    foreach ($Property in $Properties) {
        if ($Property.Name -in @(
            "vulnerabilities",
            "vulns"
        )) {
            continue
        }
        if (
            $null -ne $Property.Value -and
            $Property.Value -isnot [string] -and
            $Property.Value -isnot [ValueType]
        ) {
            Add-VulnerabilityFindings `
                -Node $Property.Value `
                -Ecosystem $Ecosystem `
                -PackageName $LocalPackage
        }
    }
}

$NugetTargets = [System.Collections.Generic.List[object]]::new()
$NugetTargets.Add(
    [pscustomobject]@{
        Name = "solution"
        Project = Join-Path $ProjectRoot "PerfMonitor.slnx"
        Report = "nuget-vulnerabilities.json"
    }
)
if ($ReleaseScope) {
    $NugetTargets.Add(
        [pscustomobject]@{
            Name = "releaseTools"
            Project = Join-Path `
                $ProjectRoot `
                (
                    "tools\PerfMonitor.ReleaseTools\" +
                    "PerfMonitor.ReleaseTools.csproj"
                )
            Report = "nuget-release-tools-vulnerabilities.json"
        }
    )
    $NugetTargets.Add(
        [pscustomobject]@{
            Name = "installer"
            Project = Join-Path `
                $ProjectRoot `
                (
                    "installer\PerfMonitor.Installer\" +
                    "PerfMonitor.Installer.wixproj"
                )
            Report = "nuget-installer-vulnerabilities.json"
        }
    )
}

$NugetReports = [System.Collections.Generic.List[object]]::new()
foreach ($Target in $NugetTargets) {
    $DotnetReportPath = Join-Path `
        $OutputDirectory `
        $Target.Report
    $DotnetOutput = (
        & dotnet package list `
            --project $Target.Project `
            --vulnerable `
            --include-transitive `
            --format json `
            --output-version 1 `
            --no-restore 2>&1
    ) | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "nuget_vulnerability_scan_failed:$($Target.Name)"
    }
    try {
        $DotnetReport = $DotnetOutput | ConvertFrom-Json
    }
    catch {
        throw "nuget_vulnerability_report_invalid:$($Target.Name)"
    }
    if (
        $DotnetReport.version -ne 1 -or
        @($DotnetReport.sources).Count -eq 0 -or
        @($DotnetReport.projects).Count -eq 0
    ) {
        throw "nuget_vulnerability_report_incomplete:$($Target.Name)"
    }
    Write-PerfMonitorUtf8NoBom `
        -Path $DotnetReportPath `
        -Value ($DotnetReport | ConvertTo-Json -Depth 100)
    Add-VulnerabilityFindings `
        -Node $DotnetReport `
        -Ecosystem "nuget"
    $NugetReports.Add(
        [ordered]@{
            scope = $Target.Name
            path = $Target.Report
            sha256 = Get-PerfMonitorSha256 `
                -Path $DotnetReportPath
        }
    )
}

$PythonReportPath = Join-Path `
    $OutputDirectory `
    "python-vulnerabilities.json"
& $AuditPythonPath -m pip_audit `
    --requirement (Join-Path $ProjectRoot "requirements-dev.txt") `
    --strict `
    --format json `
    --output $PythonReportPath `
    --progress-spinner off `
    --timeout 30
$PythonExitCode = $LASTEXITCODE
if ($PythonExitCode -notin @(0, 1)) {
    throw "python_vulnerability_scan_failed:$PythonExitCode"
}
if (-not (Test-Path -LiteralPath $PythonReportPath)) {
    throw "python_vulnerability_report_missing"
}
try {
    $PythonReport = Get-Content `
        -LiteralPath $PythonReportPath `
        -Raw |
        ConvertFrom-Json
}
catch {
    throw "python_vulnerability_report_invalid"
}
if (@($PythonReport.dependencies).Count -eq 0) {
    throw "python_vulnerability_report_incomplete"
}
$BeforePython = $Findings.Count
Add-VulnerabilityFindings `
    -Node $PythonReport `
    -Ecosystem "pypi"
$PythonFindingCount = $Findings.Count - $BeforePython
if (
    ($PythonExitCode -eq 0 -and $PythonFindingCount -ne 0) -or
    ($PythonExitCode -eq 1 -and $PythonFindingCount -eq 0)
) {
    throw "python_vulnerability_exit_report_mismatch"
}

# Advisory feeds can expose the same canonical advisory more than once
# (for example, a PyPI record and a mirrored OSV record). The gate
# keeps one finding per ecosystem/package/canonical ID while retaining
# aliases for waiver matching.
$UniqueFindings =
    [System.Collections.Generic.List[object]]::new()
$SeenFindings =
    [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
foreach ($Finding in $Findings) {
    $FindingKey = (
        $Finding.ecosystem + [char]0 +
        $Finding.package + [char]0 +
        $Finding.advisoryId
    )
    if ($SeenFindings.Add($FindingKey)) {
        $UniqueFindings.Add($Finding)
    }
}
$Findings.Clear()
foreach ($Finding in $UniqueFindings) {
    $Findings.Add($Finding)
}

try {
    $WaiverDocument = Get-Content `
        -LiteralPath $WaiverPath `
        -Raw |
        ConvertFrom-Json
}
catch {
    throw "vulnerability_waiver_report_invalid"
}
if (
    $WaiverDocument.schemaVersion -cne "1.0" -or
    $null -eq $WaiverDocument.waivers
) {
    throw "vulnerability_waiver_report_incomplete"
}
$Now = [DateTimeOffset]::UtcNow
$ActiveWaivers =
    [System.Collections.Generic.List[object]]::new()
foreach ($Waiver in @($WaiverDocument.waivers)) {
    $Expiration = [DateTimeOffset]::MinValue
    if (
        -not [DateTimeOffset]::TryParse(
            [string]$Waiver.expiresAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$Expiration
        ) -or
        $Expiration.Offset -ne [TimeSpan]::Zero
    ) {
        throw "vulnerability_waiver_expiration_invalid"
    }
    if ($Expiration -le $Now) {
        throw (
            "vulnerability_waiver_expired:" +
            $Waiver.advisoryId
        )
    }
    foreach ($RequiredText in @(
        "ecosystem",
        "package",
        "advisoryId",
        "owner",
        "impactAnalysis",
        "approvedIn"
    )) {
        if ([string]::IsNullOrWhiteSpace(
            [string]$Waiver.$RequiredText
        )) {
            throw "vulnerability_waiver_field_missing:$RequiredText"
        }
    }
    if ([string]$Waiver.impactAnalysis -and
        ([string]$Waiver.impactAnalysis).Length -lt 20) {
        throw "vulnerability_waiver_impact_too_short"
    }
    $ActiveWaivers.Add($Waiver)
}

$Unwaived =
    [System.Collections.Generic.List[object]]::new()
foreach ($Finding in $Findings) {
    $Identifiers = @($Finding.advisoryId) + @(
        $Finding.aliases
    )
    $Match = $ActiveWaivers |
        Where-Object {
            $_.ecosystem -ceq $Finding.ecosystem -and
            $_.package -ieq $Finding.package -and
            $Identifiers -contains $_.advisoryId
        } |
        Select-Object -First 1
    if ($null -eq $Match) {
        $Unwaived.Add($Finding)
    }
}

$GatePath = Join-Path `
    $OutputDirectory `
    "vulnerability-gate.json"
$DependencyInputs = Get-PerfMonitorDependencyInputHashes `
    -ProjectRoot $ProjectRoot `
    -RequireInstallerLock:$ReleaseScope
$Gate = [ordered]@{
    schemaVersion = "1.0"
    scannedAtUtc = $Now.ToString("O")
    scope = [ordered]@{
        solution = $true
        releaseTools = [bool]$ReleaseScope
        installer = [bool]$ReleaseScope
    }
    scanners = [ordered]@{
        nuget = (& dotnet --version).Trim()
        pipAudit = $AuditVersion
    }
    reports = [ordered]@{
        nuget = @($NugetReports)
        python = [ordered]@{
            path = "python-vulnerabilities.json"
            sha256 = Get-PerfMonitorSha256 `
                -Path $PythonReportPath
        }
    }
    dependencyInputs = $DependencyInputs
    findingCount = $Findings.Count
    waivedCount = $Findings.Count - $Unwaived.Count
    unwaivedCount = $Unwaived.Count
    activeWaivers = @($ActiveWaivers)
    unwaivedFindings = @($Unwaived)
    passed = $Unwaived.Count -eq 0
}
Write-PerfMonitorUtf8NoBom `
    -Path $GatePath `
    -Value ($Gate | ConvertTo-Json -Depth 20)
if ($Unwaived.Count -ne 0) {
    throw "vulnerability_gate_failed:$($Unwaived.Count)"
}

$Gate | ConvertTo-Json -Depth 20
