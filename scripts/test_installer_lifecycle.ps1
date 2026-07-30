[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentMsiPath,

    [string]$PreviousMsiPath = "",

    [Parameter(Mandatory = $true)]
    [ValidateSet("test", "production")]
    [string]$SignatureMode,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSubject,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedSha256Thumbprint,

    [string]$EvidencePath = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")
$CurrentMsiPath = Assert-PerfMonitorMsiFile `
    -Path $CurrentMsiPath
$CurrentMsiSha256 = Get-PerfMonitorSha256 `
    -Path $CurrentMsiPath
if ($PreviousMsiPath) {
    $PreviousMsiPath = Assert-PerfMonitorMsiFile `
        -Path $PreviousMsiPath
}
$PreviousMsiSha256 = if ($PreviousMsiPath) {
    Get-PerfMonitorSha256 -Path $PreviousMsiPath
}
else {
    $null
}
$ExpectedSha256Thumbprint =
    $ExpectedSha256Thumbprint.Replace(
        " ",
        ""
    ).ToUpperInvariant()
$AllowedSignatureStatuses = if (
    $SignatureMode -eq "production"
) {
    @("Valid")
}
else {
    @("Valid", "UnknownError")
}
foreach ($MsiPath in @(
    $CurrentMsiPath,
    $PreviousMsiPath
)) {
    if (-not $MsiPath) {
        continue
    }
    $Signature = Get-AuthenticodeSignature `
        -LiteralPath $MsiPath
    if (
        $Signature.Status.ToString() -notin
            $AllowedSignatureStatuses -or
        $null -eq $Signature.SignerCertificate -or
        (
            $SignatureMode -eq "production" -and
            $null -eq $Signature.TimeStamperCertificate
        ) -or
        $Signature.SignerCertificate.Subject -cne
            $ExpectedSubject -or
        (
            Get-PerfMonitorCertificateSha256 `
                -Certificate $Signature.SignerCertificate
        ) -cne $ExpectedSha256Thumbprint
    ) {
        throw "installer_lifecycle_authenticode_invalid:$MsiPath"
    }
}
$LifecycleStartedAt = [DateTimeOffset]::UtcNow
$Principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)
if (-not $Principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)) {
    throw "installer_lifecycle_requires_elevation"
}

$ProgramRoot = Join-Path `
    ${env:ProgramFiles} `
    "PerfMonitor"
$AgentPath = Join-Path `
    $ProgramRoot `
    "agent\perf-monitor-agent.exe"
$WorkerPath = Join-Path `
    $ProgramRoot `
    "agent\provider-worker\perf-monitor-provider-worker.exe"
$DesktopPath = Join-Path `
    $ProgramRoot `
    "desktop\perf-monitor-desktop.exe"
$SupportPath = Join-Path `
    $ProgramRoot `
    "support\perf-monitor-support.exe"
$BrokerPath = Join-Path `
    $ProgramRoot `
    "broker\perf-monitor-broker.exe"
$StartupShortcut = Join-Path `
    $env:ProgramData `
    (
        "Microsoft\Windows\Start Menu\Programs\Startup\" +
        "PerfMonitor Agent.lnk"
    )
$DesktopShortcut = Join-Path `
    $env:ProgramData `
    (
        "Microsoft\Windows\Start Menu\Programs\" +
        "PerfMonitor\PerfMonitor.lnk"
    )
$BrokerDataRoot = Join-Path `
    $env:ProgramData `
    "PerfMonitor\broker"
$LocalDataRoot = Join-Path `
    ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData
    )) `
    "PerfMonitor\data"
$Token = [Guid]::NewGuid().ToString("N")
$LocalMarker = Join-Path `
    $LocalDataRoot `
    "installer-lifecycle-$Token.marker"
$BrokerMarker = Join-Path `
    $BrokerDataRoot `
    "installer-lifecycle-$Token.marker"
$TempRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "perf-monitor-msi-$Token"
$AgentDataRoot = Join-Path $TempRoot "agent-data"
$AgentOutput = Join-Path $TempRoot "agent.jsonl"
$DatabasePath = Join-Path `
    $AgentDataRoot `
    "history-v1.db"
$LogsRoot = Join-Path $TempRoot "logs"
$null = New-Item -ItemType Directory -Path $LogsRoot -Force
$MsiExec = Join-Path $env:SystemRoot "System32\msiexec.exe"
$InstalledCurrent = $false
$InstalledPrevious = $false
$DowngradeBlocked = $false

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$LogName,

        [int[]]$AllowedExitCodes = @(0, 3010)
    )

    $LogPath = Join-Path $LogsRoot $LogName
    $FullArguments = @($Arguments) + @(
        "/qn",
        "/norestart",
        "/L*v",
        "`"$LogPath`""
    )
    $Process = Start-Process `
        -FilePath $MsiExec `
        -ArgumentList $FullArguments `
        -PassThru `
        -Wait `
        -WindowStyle Hidden
    if ($Process.ExitCode -notin $AllowedExitCodes) {
        $Tail = if (Test-Path -LiteralPath $LogPath) {
            (
                Get-Content -LiteralPath $LogPath |
                Select-Object -Last 80
            ) -join [Environment]::NewLine
        }
        else {
            ""
        }
        throw (
            "msiexec_failed:$($Process.ExitCode):" +
            $LogName +
            [Environment]::NewLine +
            $Tail
        )
    }
    return [pscustomobject]@{
        ExitCode = $Process.ExitCode
        LogPath = $LogPath
    }
}

function Install-Core {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogName
    )

    $null = Invoke-MsiExec `
        -Arguments @(
            "/i",
            "`"$MsiPath`"",
            "ADDLOCAL=Core"
        ) `
        -LogName $LogName
}

function Uninstall-Package {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogName
    )

    $null = Invoke-MsiExec `
        -Arguments @(
            "/x",
            "`"$MsiPath`""
        ) `
        -LogName $LogName
}

function Assert-CoreInstalled {
    foreach ($Path in @(
        $AgentPath,
        $WorkerPath,
        $DesktopPath,
        $SupportPath,
        $StartupShortcut,
        $DesktopShortcut
    )) {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "installer_core_path_missing:$Path"
        }
    }
    $BrokerService = Get-Service `
        -Name "PerfMonitorBroker" `
        -ErrorAction SilentlyContinue
    try {
        if (
            (Test-Path -LiteralPath $BrokerPath) -or
            $null -ne $BrokerService
        ) {
            throw "installer_broker_leaked_into_core"
        }
    }
    finally {
        if ($null -ne $BrokerService) {
            $BrokerService.Dispose()
        }
    }
}

function Assert-BrokerAcl {
    $Acl = Get-Acl -LiteralPath $BrokerDataRoot
    if (-not $Acl.AreAccessRulesProtected) {
        throw "installer_broker_acl_inheritance_enabled"
    }
    $SystemSid = "S-1-5-18"
    $AdministratorsSid = "S-1-5-32-544"
    $Required = @($SystemSid, $AdministratorsSid)
    foreach ($Sid in $Required) {
        $Rule = $Acl.Access |
            Where-Object {
                $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]
                ).Value -ceq $Sid -and
                $_.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                $_.FileSystemRights.HasFlag(
                    [Security.AccessControl.FileSystemRights]::FullControl
                )
            } |
            Select-Object -First 1
        if ($null -eq $Rule) {
            throw "installer_broker_acl_required_sid_missing:$Sid"
        }
    }
    foreach ($Rule in $Acl.Access) {
        $Sid = $Rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]
        ).Value
        if (
            $Sid -notin $Required -and
            $Rule.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            (
                $Rule.FileSystemRights.HasFlag(
                    [Security.AccessControl.FileSystemRights]::Write
                ) -or
                $Rule.FileSystemRights.HasFlag(
                    [Security.AccessControl.FileSystemRights]::Modify
                ) -or
                $Rule.FileSystemRights.HasFlag(
                    [Security.AccessControl.FileSystemRights]::FullControl
                )
            )
        ) {
            throw "installer_broker_acl_unexpected_writer:$Sid"
        }
    }
}

function Assert-BrokerInstalled {
    if (-not (Test-Path -LiteralPath $BrokerPath)) {
        throw "installer_broker_binary_missing"
    }
    $Service = $null
    try {
        $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            if ($null -ne $Service) {
                $Service.Dispose()
            }
            $Service = Get-Service `
                -Name "PerfMonitorBroker" `
                -ErrorAction SilentlyContinue
            if (
                $null -ne $Service -and
                $Service.Status -eq
                    [ServiceProcess.ServiceControllerStatus]::Running
            ) {
                break
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTimeOffset]::UtcNow -lt $Deadline)
        if (
            $null -eq $Service -or
            $Service.Status -ne
                [ServiceProcess.ServiceControllerStatus]::Running
        ) {
            throw "installer_broker_service_not_running"
        }
        $ServiceKey = Get-ItemProperty `
            -LiteralPath (
                "HKLM:\SYSTEM\CurrentControlSet\Services\" +
                "PerfMonitorBroker"
            )
        if (
            $ServiceKey.ObjectName -cne "LocalSystem" -or
            [int]$ServiceKey.Start -ne 2 -or
            [string]$ServiceKey.ImagePath -notlike
                (
                    "*\PerfMonitor\broker\" +
                    "perf-monitor-broker.exe*--service*"
                )
        ) {
            throw "installer_broker_service_configuration_invalid"
        }
        Assert-BrokerAcl
    }
    finally {
        if ($null -ne $Service) {
            $Service.Dispose()
        }
    }
}

function Assert-ProductRemoved {
    foreach ($Path in @(
        $AgentPath,
        $WorkerPath,
        $DesktopPath,
        $SupportPath,
        $BrokerPath,
        $StartupShortcut,
        $DesktopShortcut
    )) {
        if (Test-Path -LiteralPath $Path) {
            throw "installer_uninstall_path_remains:$Path"
        }
    }
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $Service = Get-Service `
            -Name "PerfMonitorBroker" `
            -ErrorAction SilentlyContinue
        if ($null -eq $Service) {
            return
        }
        $Service.Dispose()
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    if ($null -ne $Service) {
        throw "installer_uninstall_service_remains"
    }
}

foreach ($PreexistingPath in @(
    $AgentPath,
    $WorkerPath,
    $DesktopPath,
    $SupportPath,
    $BrokerPath,
    $StartupShortcut,
    $DesktopShortcut
)) {
    if (Test-Path -LiteralPath $PreexistingPath) {
        throw "installer_lifecycle_preexisting_product:$PreexistingPath"
    }
}
$PreexistingService = Get-Service `
    -Name "PerfMonitorBroker" `
    -ErrorAction SilentlyContinue
try {
    if ($null -ne $PreexistingService) {
        throw "installer_lifecycle_preexisting_service"
    }
}
finally {
    if ($null -ne $PreexistingService) {
        $PreexistingService.Dispose()
    }
}

try {
    if ($PreviousMsiPath) {
        Install-Core `
            -MsiPath $PreviousMsiPath `
            -LogName "01-install-previous.log"
        $InstalledPrevious = $true
    }
    else {
        Install-Core `
            -MsiPath $CurrentMsiPath `
            -LogName "01-install-current.log"
        $InstalledCurrent = $true
    }
    Assert-CoreInstalled
    & (Join-Path $PSScriptRoot "smoke.ps1") `
        -AgentPath $AgentPath `
        -DesktopPath $DesktopPath `
        -DurationSeconds 10 |
        Out-Host

    $null = New-Item `
        -ItemType Directory `
        -Path $LocalDataRoot `
        -Force
    Set-Content `
        -LiteralPath $LocalMarker `
        -Value $Token `
        -Encoding ascii
    & $AgentPath `
        --quiet `
        --warmup-seconds 0 `
        --duration-seconds 5 `
        --output-period-ms 250 `
        --data-directory $AgentDataRoot `
        --output $AgentOutput
    if ($LASTEXITCODE -ne 0) {
        throw "installer_agent_lifecycle_failed"
    }
    $Verification = (
        & $SupportPath database verify --path $DatabasePath
    ) | Select-Object -Last 1 | ConvertFrom-Json
    if (
        $LASTEXITCODE -ne 0 -or
        -not $Verification.integrityPassed
    ) {
        throw "installer_support_database_verify_failed"
    }

    if ($PreviousMsiPath) {
        $null = Invoke-MsiExec `
            -Arguments @(
                "/i",
                "`"$CurrentMsiPath`"",
                "ADDLOCAL=Core"
            ) `
            -LogName "02-upgrade-current.log"
        $InstalledPrevious = $false
        $InstalledCurrent = $true
        Assert-CoreInstalled
        & (Join-Path $PSScriptRoot "smoke.ps1") `
            -AgentPath $AgentPath `
            -DesktopPath $DesktopPath `
            -DurationSeconds 10 |
            Out-Host
        if (-not (Test-Path -LiteralPath $LocalMarker)) {
            throw "installer_upgrade_removed_user_data"
        }

        $DowngradeLog = Join-Path `
            $LogsRoot `
            "03-downgrade-blocked.log"
        $DowngradeProcess = Start-Process `
            -FilePath $MsiExec `
            -ArgumentList @(
                "/i",
                "`"$PreviousMsiPath`"",
                "/qn",
                "/norestart",
                "/L*v",
                "`"$DowngradeLog`""
            ) `
            -PassThru `
            -Wait `
            -WindowStyle Hidden
        if ($DowngradeProcess.ExitCode -eq 0) {
            throw "installer_direct_downgrade_not_blocked"
        }
        $DowngradeText = Get-Content `
            -LiteralPath $DowngradeLog `
            -Raw
        if ($DowngradeText -notlike (
            "*A newer version of PerfMonitor is already installed.*"
        )) {
            throw "installer_downgrade_message_missing"
        }
        $DowngradeBlocked = $true
    }

    Remove-Item -LiteralPath $SupportPath -Force
    $null = Invoke-MsiExec `
        -Arguments @(
            "/fa",
            "`"$CurrentMsiPath`""
        ) `
        -LogName "03b-repair-current.log"
    Assert-CoreInstalled

    $null = Invoke-MsiExec `
        -Arguments @(
            "/i",
            "`"$CurrentMsiPath`"",
            "ADDLOCAL=Core,Broker"
        ) `
        -LogName "04-add-broker.log"
    $InstalledCurrent = $true
    Assert-BrokerInstalled
    Set-Content `
        -LiteralPath $BrokerMarker `
        -Value $Token `
        -Encoding ascii

    Uninstall-Package `
        -MsiPath $CurrentMsiPath `
        -LogName "05-uninstall-current.log"
    $InstalledCurrent = $false
    Assert-ProductRemoved
    foreach ($Marker in @(
        $LocalMarker,
        $BrokerMarker
    )) {
        if (-not (Test-Path -LiteralPath $Marker)) {
            throw "installer_uninstall_removed_data:$Marker"
        }
    }

    Install-Core `
        -MsiPath $CurrentMsiPath `
        -LogName "06-reinstall-current.log"
    $InstalledCurrent = $true
    Assert-CoreInstalled
    if (
        -not (Test-Path -LiteralPath $LocalMarker) -or
        -not (Test-Path -LiteralPath $BrokerMarker)
    ) {
        throw "installer_reinstall_lost_preserved_data"
    }
    Uninstall-Package `
        -MsiPath $CurrentMsiPath `
        -LogName "07-uninstall-reinstall.log"
    $InstalledCurrent = $false
    Assert-ProductRemoved

    if ($PreviousMsiPath) {
        Install-Core `
            -MsiPath $PreviousMsiPath `
            -LogName "08-controlled-rollback.log"
        $InstalledPrevious = $true
        Assert-CoreInstalled
        if (-not (Test-Path -LiteralPath $LocalMarker)) {
            throw "installer_controlled_rollback_lost_data"
        }
        Uninstall-Package `
            -MsiPath $PreviousMsiPath `
            -LogName "09-uninstall-rollback.log"
        $InstalledPrevious = $false
        Assert-ProductRemoved
    }

    $Evidence = [ordered]@{
        schemaVersion = "1.0"
        startedAtUtc = $LifecycleStartedAt.ToString("O")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        currentMsiSha256 = $CurrentMsiSha256
        previousMsiSha256 = $PreviousMsiSha256
        signatureMode = $SignatureMode
        signerSubject = $ExpectedSubject
        signerCertificateSha256 = $ExpectedSha256Thumbprint
        passed = $true
        coreDefaultBrokerAbsent = $true
        agentDesktopSqliteLifecyclePassed = $true
        upgradePassed = [bool]$PreviousMsiPath
        directDowngradeBlocked = $DowngradeBlocked
        brokerServiceLocalSystemAuto = $true
        brokerProgramDataAclPassed = $true
        uninstallPreservedData = $true
        reinstallPassed = $true
        repairPassed = $true
        controlledRollbackPassed = [bool]$PreviousMsiPath
    }
    $Json = $Evidence | ConvertTo-Json -Depth 6
    if ($EvidencePath) {
        $EvidencePath = [System.IO.Path]::GetFullPath(
            $EvidencePath
        )
        $null = New-Item `
            -ItemType Directory `
            -Path (Split-Path -Parent $EvidencePath) `
            -Force
        $Json | Set-Content `
            -LiteralPath $EvidencePath `
            -Encoding utf8
    }
    $Json
}
finally {
    if ($InstalledCurrent) {
        try {
            Uninstall-Package `
                -MsiPath $CurrentMsiPath `
                -LogName "cleanup-current.log"
        }
        catch {
        }
    }
    if ($InstalledPrevious) {
        try {
            Uninstall-Package `
                -MsiPath $PreviousMsiPath `
                -LogName "cleanup-previous.log"
        }
        catch {
        }
    }
    foreach ($Marker in @(
        $LocalMarker,
        $BrokerMarker
    )) {
        if (
            Test-Path -LiteralPath $Marker
        ) {
            Remove-Item -LiteralPath $Marker -Force
        }
    }
    $TempRootFull = [System.IO.Path]::GetFullPath($TempRoot)
    $SystemTemp = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()
    ).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    if (
        $TempRootFull.StartsWith(
            $SystemTemp,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        (Split-Path -Leaf $TempRootFull).StartsWith(
            "perf-monitor-msi-",
            [StringComparison]::Ordinal
        ) -and
        (Test-Path -LiteralPath $TempRootFull)
    ) {
        Remove-Item `
            -LiteralPath $TempRootFull `
            -Recurse `
            -Force
    }
}
