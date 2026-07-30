[CmdletBinding()]
param(
    [string]$OutputPath = "",

    [string[]]$AdditionalArtifactPaths = @()
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

$SolutionPath = Join-Path $ProjectRoot "PerfMonitor.slnx"
$DistRoot = Join-Path $ProjectRoot "dist"
$BuildPropertiesPath = Join-Path `
    $ProjectRoot `
    "Directory.Build.props"
if (-not $OutputPath) {
    $OutputPath = Join-Path `
        $DistRoot `
        "release\PerfMonitor-sbom.cdx.json"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$OutputDirectory = Split-Path -Parent $OutputPath
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force

$BuildProperties = [xml](
    Get-Content -LiteralPath $BuildPropertiesPath -Raw
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "sbom_product_version_missing"
}
$ProductVersion = $VersionNode.InnerText.Trim()

$null = Get-Command dotnet -ErrorAction Stop
& dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "sbom_tool_restore_failed"
}

$WorkRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "perf-monitor-sbom-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $WorkRoot
$BaseName = "base.cdx.json"
$BasePath = Join-Path $WorkRoot $BaseName
try {
    & dotnet tool run dotnet-CycloneDX -- `
        $SolutionPath `
        --output $WorkRoot `
        --filename $BaseName `
        --output-format Json `
        --exclude-test-projects `
        --disable-package-restore `
        --no-serial-number `
        --set-name PerfMonitor `
        --set-version $ProductVersion `
        --spec-version 1.6
    if (
        $LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $BasePath)
    ) {
        throw "sbom_dotnet_generation_failed"
    }

    $Bom = Get-Content -LiteralPath $BasePath -Raw |
        ConvertFrom-Json
    if (
        $Bom.bomFormat -cne "CycloneDX" -or
        $Bom.specVersion -cne "1.6" -or
        $null -eq $Bom.metadata.component
    ) {
        throw "sbom_base_invalid"
    }

    $ExistingRefs = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    foreach ($Component in @($Bom.components)) {
        if ($Component.'bom-ref') {
            $null = $ExistingRefs.Add(
                [string]$Component.'bom-ref'
            )
        }
    }

    $ExtraComponents =
        [System.Collections.Generic.List[object]]::new()
    $PythonPackages = [ordered]@{}

    function Get-LogicalRequirementLines {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path
        )

        $LogicalLines =
            [System.Collections.Generic.List[string]]::new()
        $Pending = ""
        foreach ($RawLine in Get-Content -LiteralPath $Path) {
            $Line = $RawLine.Trim()
            if ($Line.EndsWith(
                "\",
                [StringComparison]::Ordinal
            )) {
                $Pending += $Line.Substring(
                    0,
                    $Line.Length - 1
                ).TrimEnd() + " "
                continue
            }
            $Pending += $Line
            $LogicalLines.Add($Pending.Trim())
            $Pending = ""
        }
        if ($Pending) {
            throw "sbom_python_requirement_continuation_invalid:$Path"
        }
        return $LogicalLines
    }

    foreach ($RequirementSpec in @(
        [pscustomobject]@{
            Path = Join-Path $ProjectRoot "requirements.txt"
            Role = "python-oracle-runtime"
        },
        [pscustomobject]@{
            Path = Join-Path $ProjectRoot "requirements-dev.txt"
            Role = "build-test"
        },
        [pscustomobject]@{
            Path = Join-Path $ProjectRoot "requirements-audit.txt"
            Role = "vulnerability-scanner"
        }
    )) {
        foreach ($Line in Get-LogicalRequirementLines `
            -Path $RequirementSpec.Path) {
            if (
                -not $Line -or
                $Line.StartsWith(
                    "#",
                    [StringComparison]::Ordinal
                ) -or
                $Line.StartsWith(
                    "-r ",
                    [StringComparison]::Ordinal
                )
            ) {
                continue
            }
            if ($Line -notmatch (
                "^([A-Za-z0-9_.-]+)" +
                "(?:\[[A-Za-z0-9_,.-]+\])?" +
                "==([^;\s]+)\s+" +
                "--hash=sha256:([0-9A-Fa-f]{64})$"
            )) {
                throw "sbom_python_requirement_not_pinned:$Line"
            }
            $Name = $Matches[1].ToLowerInvariant()
            $Version = $Matches[2]
            $WheelSha256 = $Matches[3].ToUpperInvariant()
            $PackageKey = "$Name@$Version"
            if (-not $PythonPackages.Contains($PackageKey)) {
                $PythonPackages[$PackageKey] = [pscustomobject]@{
                    Name = $Name
                    Version = $Version
                    WheelSha256 = $WheelSha256
                    Roles = [System.Collections.Generic.HashSet[string]]::new(
                        [StringComparer]::Ordinal
                    )
                }
            }
            elseif (
                $PythonPackages[$PackageKey].WheelSha256 -cne
                    $WheelSha256
            ) {
                throw "sbom_python_wheel_hash_conflict:$PackageKey"
            }
            $null = $PythonPackages[$PackageKey].Roles.Add(
                $RequirementSpec.Role
            )
        }
    }

    foreach ($PackageKey in $PythonPackages.Keys | Sort-Object) {
        $Package = $PythonPackages[$PackageKey]
        $Reference = (
            "pkg:pypi/$($Package.Name)@$($Package.Version)"
        )
        if ($ExistingRefs.Add($Reference)) {
            $ExtraComponents.Add(
                [ordered]@{
                    type = "library"
                    "bom-ref" = $Reference
                    name = $Package.Name
                    version = $Package.Version
                    scope = "required"
                    purl = $Reference
                    hashes = @(
                        [ordered]@{
                            alg = "SHA-256"
                            content = $Package.WheelSha256
                        }
                    )
                    properties = @(
                        [ordered]@{
                            name = "perfmonitor:roles"
                            value = (
                                $Package.Roles |
                                Sort-Object
                            ) -join ","
                        }
                    )
                }
            )
        }
    }

    foreach ($Tool in @(
        [pscustomobject]@{
            Type = "application"
            Name = "WixToolset.Sdk"
            Version = "7.0.0"
            Purl = "pkg:nuget/WixToolset.Sdk@7.0.0"
            Role = "installer-build-tool"
        },
        [pscustomobject]@{
            Type = "library"
            Name = "WixToolset.Netfx.wixext"
            Version = "7.0.0"
            Purl = "pkg:nuget/WixToolset.Netfx.wixext@7.0.0"
            Role = "installer-runtime-check-extension"
        },
        [pscustomobject]@{
            Type = "application"
            Name = "Microsoft.Windows.SDK.BuildTools"
            Version = "10.0.28000.2270"
            Purl = (
                "pkg:nuget/Microsoft.Windows.SDK.BuildTools" +
                "@10.0.28000.2270"
            )
            Role = "authenticode-build-tool"
        },
        [pscustomobject]@{
            Type = "application"
            Name = "CycloneDX"
            Version = "6.2.0"
            Purl = "pkg:nuget/CycloneDX@6.2.0"
            Role = "sbom-build-tool"
        }
    )) {
        if ($ExistingRefs.Add($Tool.Purl)) {
            $ExtraComponents.Add(
                [ordered]@{
                    type = $Tool.Type
                    "bom-ref" = $Tool.Purl
                    name = $Tool.Name
                    version = $Tool.Version
                    scope = "required"
                    purl = $Tool.Purl
                    properties = @(
                        [ordered]@{
                            name = "perfmonitor:role"
                            value = $Tool.Role
                        }
                    )
                }
            )
        }
    }

    $PayloadRoots = @(
        "agent",
        "desktop",
        "broker",
        "support"
    ) |
        ForEach-Object { Join-Path $DistRoot $_ } |
        Where-Object { Test-Path -LiteralPath $_ }
    $ArtifactFiles =
        [System.Collections.Generic.List[string]]::new()
    foreach ($PayloadRoot in $PayloadRoots) {
        foreach ($File in Get-ChildItem `
            -LiteralPath $PayloadRoot `
            -File `
            -Recurse) {
            $ArtifactFiles.Add($File.FullName)
        }
    }
    foreach ($ArtifactPath in $AdditionalArtifactPaths) {
        $ArtifactFiles.Add(
            (Resolve-Path -LiteralPath $ArtifactPath).Path
        )
    }

    foreach ($ArtifactPath in $ArtifactFiles |
        Sort-Object -Unique) {
        $Hash = Get-PerfMonitorSha256 -Path $ArtifactPath
        $Reference = "urn:sha256:$Hash"
        if (-not $ExistingRefs.Add($Reference)) {
            continue
        }
        $RelativePath = if (
            $ArtifactPath.StartsWith(
                $ProjectRoot +
                    [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase
            )
        ) {
            $ArtifactPath.Substring(
                $ProjectRoot.Length + 1
            ).Replace("\", "/")
        }
        else {
            [System.IO.Path]::GetFileName($ArtifactPath)
        }
        $ExtraComponents.Add(
            [ordered]@{
                type = "file"
                "bom-ref" = $Reference
                name = [System.IO.Path]::GetFileName(
                    $ArtifactPath
                )
                version = $ProductVersion
                hashes = @(
                    [ordered]@{
                        alg = "SHA-256"
                        content = $Hash
                    }
                )
                properties = @(
                    [ordered]@{
                        name = "perfmonitor:path"
                        value = $RelativePath
                    }
                    [ordered]@{
                        name = "perfmonitor:sizeBytes"
                        value = (
                            Get-Item -LiteralPath $ArtifactPath
                        ).Length.ToString(
                            [Globalization.CultureInfo]::InvariantCulture
                        )
                    }
                )
            }
        )
    }

    $Bom.components = @($Bom.components) + @($ExtraComponents)
    $Commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $Commit) {
        throw "sbom_git_commit_missing"
    }
    $Bom.metadata.component | Add-Member `
        -MemberType NoteProperty `
        -Name properties `
        -Value @(
            [ordered]@{
                name = "perfmonitor:gitCommit"
                value = $Commit
            },
            [ordered]@{
                name = "perfmonitor:runtimeIdentifier"
                value = "win-x64"
            },
            [ordered]@{
                name = "perfmonitor:contractVersion"
                value = "1.0"
            }
        ) `
        -Force

    $Json = $Bom | ConvertTo-Json -Depth 100
    Write-PerfMonitorUtf8NoBom `
        -Path $OutputPath `
        -Value $Json
    $RoundTrip = Get-Content -LiteralPath $OutputPath -Raw |
        ConvertFrom-Json
    if (
        $RoundTrip.bomFormat -cne "CycloneDX" -or
        @($RoundTrip.components).Count -le 0 -or
        -not (@($RoundTrip.components).name -contains
            "pip-audit") -or
        -not (@($RoundTrip.components).name -contains
            "WixToolset.Sdk")
    ) {
        throw "sbom_round_trip_validation_failed"
    }

    [ordered]@{
        path = $OutputPath
        sha256 = Get-PerfMonitorSha256 -Path $OutputPath
        componentCount = @($RoundTrip.components).Count
    } | ConvertTo-Json
}
finally {
    $WorkRootFull = [System.IO.Path]::GetFullPath($WorkRoot)
    $TempRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()
    ).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if (
        $WorkRootFull.StartsWith(
            $TempRoot,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        (Split-Path -Leaf $WorkRootFull).StartsWith(
            "perf-monitor-sbom-",
            [StringComparison]::Ordinal
        ) -and
        (Test-Path -LiteralPath $WorkRootFull)
    ) {
        Remove-Item `
            -LiteralPath $WorkRootFull `
            -Recurse `
            -Force
    }
}
