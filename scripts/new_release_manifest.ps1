[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("test", "production")]
    [string]$SignatureMode,

    [Parameter(Mandatory = $true)]
    [string]$PayloadSigningEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerSigningEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$SbomPath,

    [Parameter(Mandatory = $true)]
    [string]$VulnerabilityGatePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerContractEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerLifecycleEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$CrashRecoveryEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$ResourceEvidencePath,

    [string]$Baseline72HourEvidencePath = "",

    [string]$SupportMatrixEvidencePath = "",

    [Parameter(Mandatory = $true)]
    [string]$UpdateManifestPath,

    [string]$UpdateSignaturePath = "",

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")

foreach ($VariableName in @(
    "PayloadSigningEvidencePath",
    "InstallerSigningEvidencePath",
    "InstallerPath",
    "SbomPath",
    "VulnerabilityGatePath",
    "InstallerContractEvidencePath",
    "InstallerLifecycleEvidencePath",
    "CrashRecoveryEvidencePath",
    "ResourceEvidencePath",
    "UpdateManifestPath"
)) {
    Set-Variable `
        -Name $VariableName `
        -Value (
            Resolve-Path `
                -LiteralPath (
                    Get-Variable -Name $VariableName -ValueOnly
                )
        ).Path
}
$InstallerPath = Assert-PerfMonitorMsiFile -Path $InstallerPath
if (-not $UpdateSignaturePath) {
    $UpdateSignaturePath = $UpdateManifestPath + ".p7s"
}
$UpdateSignaturePath = (
    Resolve-Path -LiteralPath $UpdateSignaturePath
).Path
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if ($Baseline72HourEvidencePath) {
    $Baseline72HourEvidencePath = (
        Resolve-Path -LiteralPath $Baseline72HourEvidencePath
    ).Path
}
if ($SupportMatrixEvidencePath) {
    $SupportMatrixEvidencePath = (
        Resolve-Path -LiteralPath $SupportMatrixEvidencePath
    ).Path
}

function Read-JsonDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    try {
        return Get-Content -LiteralPath $Path -Raw |
            ConvertFrom-Json
    }
    catch {
        throw $ErrorCode
    }
}

function Get-RelativeReleasePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $FullPath = [System.IO.Path]::GetFullPath($Path)
    $Prefix = $ProjectRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if ($FullPath.StartsWith(
        $Prefix,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        return $FullPath.Substring(
            $ProjectRoot.Length + 1
        ).Replace("\", "/")
    }
    return [System.IO.Path]::GetFileName($FullPath)
}

function New-FileDigest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Kind = ""
    )

    $Item = Get-Item -LiteralPath $Path
    $Digest = [ordered]@{
        path = Get-RelativeReleasePath -Path $Item.FullName
        sha256 = Get-PerfMonitorSha256 -Path $Item.FullName
        sizeBytes = $Item.Length
    }
    if ($Kind) {
        $Digest.kind = $Kind
    }
    return $Digest
}

$PayloadEvidence = Read-JsonDocument `
    -Path $PayloadSigningEvidencePath `
    -ErrorCode "payload_signing_evidence_invalid"
$InstallerEvidence = Read-JsonDocument `
    -Path $InstallerSigningEvidencePath `
    -ErrorCode "installer_signing_evidence_invalid"
foreach ($Evidence in @(
    $PayloadEvidence,
    $InstallerEvidence
)) {
    if (
        $Evidence.schemaVersion -cne "1.0" -or
        $Evidence.signatureMode -cne $SignatureMode -or
        [string]::IsNullOrWhiteSpace(
            [string]$Evidence.subject
        ) -or
        [string]$Evidence.certificateSha256 -notmatch
            "^[0-9A-F]{64}$"
    ) {
        throw "signing_evidence_metadata_invalid"
    }
    if (
        (
            $SignatureMode -eq "production" -and
            (
                [string]$Evidence.timestampUrl -notmatch
                    "^https://" -or
                [string]$Evidence.timestampUrl -match "[`r`n]"
            )
        ) -or
        (
            $SignatureMode -eq "test" -and
            $null -ne $Evidence.timestampUrl
        )
    ) {
        throw "signing_evidence_timestamp_invalid"
    }
}
if (
    $PayloadEvidence.subject -cne
        $InstallerEvidence.subject -or
    $PayloadEvidence.certificateSha256 -cne
        $InstallerEvidence.certificateSha256
) {
    throw "signing_evidence_signer_mismatch"
}

$ExpectedEntrypoints = @(
    "dist\agent\perf-monitor-agent.exe",
    (
        "dist\agent\provider-worker\" +
        "perf-monitor-provider-worker.exe"
    ),
    "dist\desktop\perf-monitor-desktop.exe",
    "dist\broker\perf-monitor-broker.exe",
    "dist\support\perf-monitor-support.exe"
)
foreach ($RelativePath in $ExpectedEntrypoints) {
    $Path = Join-Path $ProjectRoot $RelativePath
    $Hash = Get-PerfMonitorSha256 -Path $Path
    if (
        -not (
            @($PayloadEvidence.files).sha256 -contains $Hash
        )
    ) {
        throw "payload_signing_evidence_file_missing:$RelativePath"
    }
    $PayloadSignature = Get-AuthenticodeSignature `
        -LiteralPath $Path
    $AllowedPayloadStatuses = if (
        $SignatureMode -eq "production"
    ) {
        @("Valid")
    }
    else {
        @("Valid", "UnknownError")
    }
    if (
        $PayloadSignature.Status.ToString() -notin
            $AllowedPayloadStatuses -or
        $null -eq $PayloadSignature.SignerCertificate -or
        (
            $SignatureMode -eq "production" -and
            $null -eq $PayloadSignature.TimeStamperCertificate
        ) -or
        $PayloadSignature.SignerCertificate.Subject -cne
            $PayloadEvidence.subject -or
        (
            Get-PerfMonitorCertificateSha256 `
                -Certificate (
                    $PayloadSignature.SignerCertificate
                )
        ) -cne $PayloadEvidence.certificateSha256
    ) {
        throw "payload_authenticode_invalid:$RelativePath"
    }
}
$InstallerHash = Get-PerfMonitorSha256 -Path $InstallerPath
if (
    -not (
        @($InstallerEvidence.files).sha256 -contains
            $InstallerHash
    )
) {
    throw "installer_signing_evidence_file_missing"
}

$InstallerSignature = Get-AuthenticodeSignature `
    -LiteralPath $InstallerPath
$AllowedSignatureStatuses = if (
    $SignatureMode -eq "production"
) {
    @("Valid")
}
else {
    @("Valid", "UnknownError")
}
if (
    $InstallerSignature.Status.ToString() -notin
        $AllowedSignatureStatuses -or
    $null -eq $InstallerSignature.SignerCertificate -or
    (
        $SignatureMode -eq "production" -and
        $null -eq $InstallerSignature.TimeStamperCertificate
    ) -or
    (
        Get-PerfMonitorCertificateSha256 `
            -Certificate $InstallerSignature.SignerCertificate
    ) -cne $PayloadEvidence.certificateSha256
) {
    throw "installer_authenticode_invalid"
}

$Sbom = Read-JsonDocument `
    -Path $SbomPath `
    -ErrorCode "release_sbom_invalid"
if (
    $Sbom.bomFormat -cne "CycloneDX" -or
    $Sbom.specVersion -cne "1.6" -or
    @($Sbom.components).Count -eq 0
) {
    throw "release_sbom_incomplete"
}
$VulnerabilityGate = Read-JsonDocument `
    -Path $VulnerabilityGatePath `
    -ErrorCode "release_vulnerability_gate_invalid"
if (
    -not $VulnerabilityGate.passed -or
    [int]$VulnerabilityGate.unwaivedCount -ne 0 -or
    -not $VulnerabilityGate.scope.solution -or
    -not $VulnerabilityGate.scope.releaseTools -or
    -not $VulnerabilityGate.scope.installer
) {
    throw "release_vulnerability_gate_failed"
}
$ExpectedDependencyInputs =
    Get-PerfMonitorDependencyInputHashes `
        -ProjectRoot $ProjectRoot `
        -RequireInstallerLock
$ActualDependencyProperties = @(
    $VulnerabilityGate.dependencyInputs.PSObject.Properties
)
if (
    $ActualDependencyProperties.Count -ne
        $ExpectedDependencyInputs.Count
) {
    throw "release_vulnerability_input_count_mismatch"
}
foreach ($DependencyPath in $ExpectedDependencyInputs.Keys) {
    $ActualProperty =
        $VulnerabilityGate.dependencyInputs.PSObject.Properties[
            $DependencyPath
        ]
    if (
        $null -eq $ActualProperty -or
        [string]$ActualProperty.Value -cne
            $ExpectedDependencyInputs[$DependencyPath]
    ) {
        throw "release_vulnerability_input_mismatch:$DependencyPath"
    }
}
$InstallerContract = Read-JsonDocument `
    -Path $InstallerContractEvidencePath `
    -ErrorCode "release_installer_contract_invalid"
$InstallerLifecycle = Read-JsonDocument `
    -Path $InstallerLifecycleEvidencePath `
    -ErrorCode "release_installer_lifecycle_invalid"
$CrashRecovery = Read-JsonDocument `
    -Path $CrashRecoveryEvidencePath `
    -ErrorCode "release_crash_recovery_invalid"
$ResourceEvidence = Read-JsonDocument `
    -Path $ResourceEvidencePath `
    -ErrorCode "release_resource_evidence_invalid"
if (
    $InstallerContract.schemaVersion -cne "1.0" -or
    -not $InstallerContract.passed -or
    $InstallerContract.msiSha256 -cne $InstallerHash -or
    $InstallerContract.signatureMode -cne $SignatureMode -or
    $InstallerContract.manufacturer -cne
        $PayloadEvidence.subject
) {
    throw "release_installer_contract_failed"
}
if (
    $InstallerLifecycle.schemaVersion -cne "1.0" -or
    $InstallerLifecycle.currentMsiSha256 -cne
        $InstallerHash -or
    $InstallerLifecycle.signatureMode -cne
        $SignatureMode -or
    $InstallerLifecycle.signerSubject -cne
        $PayloadEvidence.subject -or
    $InstallerLifecycle.signerCertificateSha256 -cne
        $PayloadEvidence.certificateSha256 -or
    [string]::IsNullOrWhiteSpace(
        [string]$InstallerLifecycle.previousMsiSha256
    )
) {
    throw "release_installer_lifecycle_binding_failed"
}
foreach ($LifecycleProperty in @(
    "passed",
    "coreDefaultBrokerAbsent",
    "agentDesktopSqliteLifecyclePassed",
    "upgradePassed",
    "directDowngradeBlocked",
    "brokerServiceLocalSystemAuto",
    "brokerProgramDataAclPassed",
    "uninstallPreservedData",
    "reinstallPassed",
    "repairPassed",
    "controlledRollbackPassed"
)) {
    if (-not [bool]$InstallerLifecycle.$LifecycleProperty) {
        throw (
            "release_installer_lifecycle_failed:" +
            $LifecycleProperty
        )
    }
}
foreach ($RecoveryProperty in @(
    "passed",
    "walObservedBeforeTermination",
    "integrityPassedAfterTermination",
    "integrityPassedAfterRestart",
    "instanceChanged"
)) {
    if (-not [bool]$CrashRecovery.$RecoveryProperty) {
        throw (
            "release_crash_recovery_failed:" +
            $RecoveryProperty
        )
    }
}
$AgentPath = Join-Path `
    $ProjectRoot `
    "dist\agent\perf-monitor-agent.exe"
$SupportPath = Join-Path `
    $ProjectRoot `
    "dist\support\perf-monitor-support.exe"
$AgentHash = Get-PerfMonitorSha256 -Path $AgentPath
$SupportHash = Get-PerfMonitorSha256 -Path $SupportPath
if (
    $CrashRecovery.schemaVersion -cne "1.0" -or
    [int]$CrashRecovery.databaseSchemaVersion -ne 2 -or
    $CrashRecovery.agentSha256 -cne $AgentHash -or
    $CrashRecovery.supportSha256 -cne $SupportHash
) {
    throw "release_crash_recovery_binding_failed"
}
if (
    $ResourceEvidence.contractVersion -cne "1.0" -or
    -not $ResourceEvidence.passed -or
    -not $ResourceEvidence.resources.passed -or
    $ResourceEvidence.agentSha256 -cne $AgentHash
) {
    throw "release_resource_evidence_failed"
}
$Baseline72Hour = $null
if ($Baseline72HourEvidencePath) {
    $Baseline72Hour = Read-JsonDocument `
        -Path $Baseline72HourEvidencePath `
        -ErrorCode "release_baseline_72h_evidence_invalid"
    if (
        $Baseline72Hour.contractVersion -cne "1.0" -or
        $Baseline72Hour.profile -cne "release-72h" -or
        [int]$Baseline72Hour.requestedDurationSeconds -lt
            72 * 60 * 60 -or
        [double]$Baseline72Hour.actualElapsedSeconds -lt
            72 * 60 * 60 -or
        -not $Baseline72Hour.passed -or
        -not (
            $Baseline72Hour.release72HourGate.actualWallClockPassed
        )
    ) {
        throw "release_baseline_72h_evidence_failed"
    }
}
elseif ($SignatureMode -eq "production") {
    throw "production_release_requires_baseline_72h_evidence"
}
$SupportMatrix = $null
if ($SupportMatrixEvidencePath) {
    $SupportMatrix = Read-JsonDocument `
        -Path $SupportMatrixEvidencePath `
        -ErrorCode "release_support_matrix_evidence_invalid"
}
elseif ($SignatureMode -eq "production") {
    throw "production_release_requires_support_matrix_evidence"
}
$UpdateManifest = Read-JsonDocument `
    -Path $UpdateManifestPath `
    -ErrorCode "release_update_manifest_invalid"
if (
    $UpdateManifest.signer.mode -cne $SignatureMode -or
    $UpdateManifest.signer.subject -cne
        $PayloadEvidence.subject -or
    $UpdateManifest.signer.certificateSha256 -cne
        $PayloadEvidence.certificateSha256 -or
    $UpdateManifest.installer.sha256 -cne
        $InstallerHash -or
    $UpdateManifest.sbom.sha256 -cne (
        Get-PerfMonitorSha256 -Path $SbomPath
    ) -or
    $InstallerContract.productVersion -cne
        $UpdateManifest.version -or
    $CrashRecovery.productVersion -cne
        $UpdateManifest.version -or
    $ResourceEvidence.productVersion -cne
        $UpdateManifest.version
) {
    throw "release_update_manifest_binding_invalid"
}
& (Join-Path $PSScriptRoot "test_update_manifest.ps1") `
    -ManifestPath $UpdateManifestPath `
    -SignaturePath $UpdateSignaturePath `
    -AllowedMode $SignatureMode `
    -ExpectedSubject $PayloadEvidence.subject `
    -ExpectedSha256Thumbprint (
        $PayloadEvidence.certificateSha256
    ) `
    -InstallerPath $InstallerPath `
    -SbomPath $SbomPath |
    Out-Null

$BuildProperties = [xml](
    Get-Content -LiteralPath (
        Join-Path $ProjectRoot "Directory.Build.props"
    ) -Raw
)
$VersionNode = $BuildProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version"
)
if ($null -eq $VersionNode) {
    throw "release_product_version_missing"
}
$ProductVersion = $VersionNode.InnerText.Trim()
$GitCommit = (& git rev-parse HEAD).Trim()
if (
    $LASTEXITCODE -ne 0 -or
    $GitCommit -notmatch "^[0-9a-f]{40}$"
) {
    throw "release_git_commit_invalid"
}
$DotnetSdkVersion = (& dotnet --version).Trim()
if (
    $LASTEXITCODE -ne 0 -or
    [string]::IsNullOrWhiteSpace($DotnetSdkVersion)
) {
    throw "release_dotnet_sdk_version_invalid"
}
$GitDirty = [bool](& git status --porcelain)
if (
    $SignatureMode -eq "production" -and
    $GitDirty
) {
    throw "production_release_requires_clean_git"
}
if (
    $ProductVersion -cne $UpdateManifest.version -or
    $Sbom.metadata.component.version -cne $ProductVersion
) {
    throw "release_product_version_binding_invalid"
}
if ($SupportMatrixEvidencePath) {
    & (Join-Path `
        $PSScriptRoot `
        "test_support_matrix_evidence.ps1") `
        -EvidencePath $SupportMatrixEvidencePath `
        -ExpectedProductVersion $ProductVersion `
        -ExpectedGitCommit $GitCommit |
        Out-Null
}
$SbomGitProperty = @(
    $Sbom.metadata.component.properties |
        Where-Object {
            $_.name -ceq "perfmonitor:gitCommit"
        }
) | Select-Object -First 1
if (
    $null -eq $SbomGitProperty -or
    $SbomGitProperty.value -cne $GitCommit
) {
    throw "release_sbom_git_binding_invalid"
}
$SbomFileHashes =
    [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
foreach ($Component in @($Sbom.components)) {
    if ($Component.type -cne "file") {
        continue
    }
    foreach ($Hash in @($Component.hashes)) {
        if (
            $Hash.alg -ceq "SHA-256" -and
            [string]$Hash.content -match "^[0-9A-Fa-f]{64}$"
        ) {
            $null = $SbomFileHashes.Add(
                ([string]$Hash.content).ToUpperInvariant()
            )
        }
    }
}
$ExpectedSbomFiles =
    [System.Collections.Generic.List[string]]::new()
foreach ($PayloadFolder in @(
    "agent",
    "desktop",
    "broker",
    "support"
)) {
    foreach ($PayloadFile in Get-ChildItem `
        -LiteralPath (
            Join-Path (
                Join-Path $ProjectRoot "dist"
            ) $PayloadFolder
        ) `
        -File `
        -Recurse) {
        $ExpectedSbomFiles.Add($PayloadFile.FullName)
    }
}
$ExpectedSbomFiles.Add($InstallerPath)
foreach ($ExpectedSbomFile in $ExpectedSbomFiles) {
    $ExpectedHash = Get-PerfMonitorSha256 `
        -Path $ExpectedSbomFile
    if (-not $SbomFileHashes.Contains($ExpectedHash)) {
        throw (
            "release_sbom_file_missing:" +
            (Get-RelativeReleasePath -Path $ExpectedSbomFile)
        )
    }
}

$Files = [System.Collections.Generic.List[object]]::new()
foreach ($Folder in @(
    "agent",
    "desktop",
    "broker",
    "support"
)) {
    $Root = Join-Path (Join-Path $ProjectRoot "dist") $Folder
    foreach ($File in Get-ChildItem `
        -LiteralPath $Root `
        -File `
        -Recurse) {
        $Files.Add(
            (New-FileDigest `
                -Path $File.FullName `
                -Kind "payload")
        )
    }
}
foreach ($Artifact in @(
    [pscustomobject]@{
        Path = $InstallerPath
        Kind = "installer"
    },
    [pscustomobject]@{
        Path = $SbomPath
        Kind = "metadata"
    },
    [pscustomobject]@{
        Path = $VulnerabilityGatePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $InstallerContractEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $InstallerLifecycleEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $CrashRecoveryEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $ResourceEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $PayloadSigningEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $InstallerSigningEvidencePath
        Kind = "evidence"
    },
    [pscustomobject]@{
        Path = $UpdateManifestPath
        Kind = "metadata"
    },
    [pscustomobject]@{
        Path = $UpdateSignaturePath
        Kind = "metadata"
    }
)) {
    $Files.Add(
        (New-FileDigest `
            -Path $Artifact.Path `
            -Kind $Artifact.Kind)
    )
}
if ($Baseline72HourEvidencePath) {
    $Files.Add(
        (New-FileDigest `
            -Path $Baseline72HourEvidencePath `
            -Kind "evidence")
    )
}
if ($SupportMatrixEvidencePath) {
    $Files.Add(
        (New-FileDigest `
            -Path $SupportMatrixEvidencePath `
            -Kind "evidence")
    )
}

$DependencyLockHashes = $ExpectedDependencyInputs

$Manifest = [ordered]@{
    schemaVersion = "1.0"
    productVersion = $ProductVersion
    contractVersion = "1.0"
    runtimeIdentifier = "win-x64"
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gitCommit = $GitCommit
    gitDirty = $GitDirty
    signature = [ordered]@{
        mode = $SignatureMode
        subject = $PayloadEvidence.subject
        certificateSha256 =
            $PayloadEvidence.certificateSha256
        payloadEvidenceSha256 =
            Get-PerfMonitorSha256 `
                -Path $PayloadSigningEvidencePath
        installerEvidenceSha256 =
            Get-PerfMonitorSha256 `
                -Path $InstallerSigningEvidencePath
    }
    sbom = New-FileDigest -Path $SbomPath
    vulnerabilityGate = [ordered]@{
        path = Get-RelativeReleasePath `
            -Path $VulnerabilityGatePath
        sha256 = Get-PerfMonitorSha256 `
            -Path $VulnerabilityGatePath
        sizeBytes = (
            Get-Item -LiteralPath $VulnerabilityGatePath
        ).Length
        passed = $true
        findingCount = [int]$VulnerabilityGate.findingCount
        waivedCount = [int]$VulnerabilityGate.waivedCount
    }
    gates = [ordered]@{
        installerContract = New-FileDigest `
            -Path $InstallerContractEvidencePath
        installerLifecycle = New-FileDigest `
            -Path $InstallerLifecycleEvidencePath
        crashRecovery = New-FileDigest `
            -Path $CrashRecoveryEvidencePath
        resourceBudget = New-FileDigest `
            -Path $ResourceEvidencePath
    }
    baseline72Hour = if ($Baseline72HourEvidencePath) {
        New-FileDigest -Path $Baseline72HourEvidencePath
    }
    else {
        $null
    }
    supportMatrix = if ($SupportMatrixEvidencePath) {
        New-FileDigest -Path $SupportMatrixEvidencePath
    }
    else {
        $null
    }
    updateManifest = [ordered]@{
        manifest = New-FileDigest -Path $UpdateManifestPath
        signature = New-FileDigest `
            -Path $UpdateSignaturePath
    }
    files = @(
        $Files |
        Sort-Object {
            $_.path
        }
    )
    dependencyLockHashes = $DependencyLockHashes
    toolchain = [ordered]@{
        dotnetSdk = $DotnetSdkVersion
        cycloneDx = "6.2.0"
        pipAudit = "2.10.1"
        wix = "7.0.0"
        signToolPackage = "10.0.28000.2270"
    }
    candidateEligible = (
        $SignatureMode -eq "production" -and
        -not $GitDirty -and
        $null -ne $Baseline72Hour -and
        $null -ne $SupportMatrix
    )
    provenanceAttestationRequired = $true
}
$OutputDirectory = Split-Path -Parent $OutputPath
$null = New-Item `
    -ItemType Directory `
    -Path $OutputDirectory `
    -Force
Write-PerfMonitorUtf8NoBom `
    -Path $OutputPath `
    -Value ($Manifest | ConvertTo-Json -Depth 30)
$Manifest | ConvertTo-Json -Depth 30
