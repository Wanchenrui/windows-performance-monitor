[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [ValidateSet("unsigned", "test", "production")]
    [string]$SignatureMode = "unsigned",

    [string]$ExpectedSubject = "",

    [string]$ExpectedSha256Thumbprint = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release_common.ps1")
$MsiPath = Assert-PerfMonitorMsiFile -Path $MsiPath

if ($SignatureMode -ne "unsigned") {
    if (
        -not $ExpectedSubject -or
        $ExpectedSha256Thumbprint -notmatch
            "^[0-9A-Fa-f]{64}$"
    ) {
        throw "installer_contract_signer_pin_required"
    }
    $Signature = Get-AuthenticodeSignature `
        -LiteralPath $MsiPath
    $AllowedStatuses = if (
        $SignatureMode -eq "production"
    ) {
        @("Valid")
    }
    else {
        @("Valid", "UnknownError")
    }
    if (
        $Signature.Status.ToString() -notin
            $AllowedStatuses -or
        $null -eq $Signature.SignerCertificate -or
        $Signature.SignerCertificate.Subject -cne
            $ExpectedSubject -or
        (
            Get-PerfMonitorCertificateSha256 `
                -Certificate $Signature.SignerCertificate
        ) -cne (
            $ExpectedSha256Thumbprint.Replace(
                " ",
                ""
            ).ToUpperInvariant()
        )
    ) {
        throw "installer_contract_authenticode_invalid"
    }
}

function Open-MsiDatabase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $Installer = New-Object -ComObject WindowsInstaller.Installer
    $Database = $Installer.GetType().InvokeMember(
        "OpenDatabase",
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Installer,
        @($Path, 0)
    )
    return [pscustomobject]@{
        Installer = $Installer
        Database = $Database
    }
}

function Invoke-MsiQuery {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Sql,

        [Parameter(Mandatory = $true)]
        [int]$ColumnCount
    )

    $View = $Database.OpenView($Sql)
    $null = $View.Execute()
    $Rows = [System.Collections.Generic.List[object]]::new()
    try {
        while ($null -ne ($Record = $View.Fetch())) {
            $Values = @()
            for ($Index = 1; $Index -le $ColumnCount; $Index++) {
                $Values += $Record.StringData($Index)
            }
            $Rows.Add(
                [pscustomobject]@{
                    Values = [object[]]$Values
                }
            )
        }
    }
    finally {
        $null = $View.Close()
    }
    return $Rows.ToArray()
}

$Opened = Open-MsiDatabase -Path $MsiPath
try {
    $Properties = @{}
    foreach ($Row in Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Property`,`Value` FROM `Property`'
        ) `
        -ColumnCount 2) {
        $Properties[$Row.Values[0]] = $Row.Values[1]
    }
    foreach ($RequiredProperty in @(
        "ProductCode",
        "ProductVersion",
        "Manufacturer",
        "UpgradeCode"
    )) {
        if (-not $Properties.ContainsKey($RequiredProperty)) {
            throw "installer_contract_property_missing:$RequiredProperty"
        }
    }
    if (
        $Properties.UpgradeCode -cne
            "{97E0D462-D2F8-43A5-B56F-06152D4D502D}"
    ) {
        throw "installer_contract_upgrade_code_invalid"
    }

    $Features = @{}
    foreach ($Row in Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql 'SELECT `Feature`,`Level` FROM `Feature`' `
        -ColumnCount 2) {
        $Features[$Row.Values[0]] = [int]$Row.Values[1]
    }
    if (
        $Features.Core -ne 1 -or
        $Features.Broker -ne 101
    ) {
        throw "installer_contract_feature_levels_invalid"
    }

    $Services = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Name`,`ServiceType`,`StartType`,' +
            '`ErrorControl`,`StartName`,`Arguments` ' +
            'FROM `ServiceInstall`'
        ) `
        -ColumnCount 6
    if (
        $Services.Count -ne 1 -or
        $Services[0].Values[0] -cne "PerfMonitorBroker" -or
        [int]$Services[0].Values[1] -ne 16 -or
        [int]$Services[0].Values[2] -ne 2 -or
        [int]$Services[0].Values[3] -ne 32769 -or
        $Services[0].Values[4] -cne "LocalSystem" -or
        $Services[0].Values[5] -cne "--service"
    ) {
        throw "installer_contract_service_invalid"
    }
    $ServiceControls = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Name`,`Event`,`Wait` ' +
            'FROM `ServiceControl`'
        ) `
        -ColumnCount 3
    if (
        $ServiceControls.Count -ne 1 -or
        $ServiceControls[0].Values[0] -cne
            "PerfMonitorBroker" -or
        [int]$ServiceControls[0].Values[1] -ne 163 -or
        [int]$ServiceControls[0].Values[2] -ne 1
    ) {
        throw "installer_contract_service_control_invalid"
    }

    $Shortcuts = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Shortcut`,`Directory_`,`Arguments` ' +
            'FROM `Shortcut`'
        ) `
        -ColumnCount 3
    $ShortcutIds = @(
        $Shortcuts |
            ForEach-Object { $_.Values[0] }
    )
    if (
        $ShortcutIds.Count -ne 2 -or
        $ShortcutIds -notcontains "AgentStartupShortcut" -or
        $ShortcutIds -notcontains
            "DesktopStartMenuShortcut"
    ) {
        throw "installer_contract_shortcuts_invalid"
    }
    $StartupShortcut = $Shortcuts |
        Where-Object {
            $_.Values[0] -ceq "AgentStartupShortcut"
        } |
        Select-Object -First 1
    if (
        $StartupShortcut.Values[1] -cne
            "CommonStartupFolder" -or
        $StartupShortcut.Values[2] -cne "--quiet"
    ) {
        throw "installer_contract_startup_shortcut_invalid"
    }

    $Permissions = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Table`,`LockObject`,`SDDLText` ' +
            'FROM `MsiLockPermissionsEx`'
        ) `
        -ColumnCount 3
    if (
        -not ($Permissions | Where-Object {
            $_.Values[0] -ceq "CreateFolder" -and
            $_.Values[2] -ceq
                "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
        })
    ) {
        throw "installer_contract_broker_acl_invalid"
    }

    $LaunchConditions = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Condition`,`Description` ' +
            'FROM `LaunchCondition`'
        ) `
        -ColumnCount 2
    if (
        -not ($LaunchConditions | Where-Object {
            $_.Values[0] -like
                "*DOTNET_DESKTOP_RUNTIME_CHECK = 0*"
        }) -or
        -not ($LaunchConditions | Where-Object {
            $_.Values[0] -like
                "*WINDOWS_CURRENT_BUILD >= 26100*"
        }) -or
        -not ($LaunchConditions | Where-Object {
            $_.Values[0] -like
                '*WINDOWS_INSTALLATION_TYPE = "Server"*'
        })
    ) {
        throw "installer_contract_launch_conditions_invalid"
    }

    $Files = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql 'SELECT `File` FROM `File`' `
        -ColumnCount 1
    $FileIds = @(
        $Files |
            ForEach-Object { $_.Values[0] }
    )
    foreach ($RequiredFileId in @(
        "AgentExecutable",
        "DesktopExecutable",
        "BrokerExecutable",
        "SupportExecutable"
    )) {
        if ($FileIds -notcontains $RequiredFileId) {
            throw "installer_contract_file_missing:$RequiredFileId"
        }
    }

    $UpgradeRows = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql 'SELECT `UpgradeCode` FROM `Upgrade`' `
        -ColumnCount 1
    if ($UpgradeRows.Count -lt 2) {
        throw "installer_contract_major_upgrade_missing"
    }

    $FeatureComponents = Invoke-MsiQuery `
        -Database $Opened.Database `
        -Sql (
            'SELECT `Feature_`,`Component_` ' +
            'FROM `FeatureComponents`'
        ) `
        -ColumnCount 2
    foreach ($ExpectedMapping in @(
        [pscustomobject]@{
            Feature = "Core"
            Component = "AgentExecutableComponent"
        },
        [pscustomobject]@{
            Feature = "Core"
            Component = "DesktopExecutableComponent"
        },
        [pscustomobject]@{
            Feature = "Core"
            Component = "SupportExecutableComponent"
        },
        [pscustomobject]@{
            Feature = "Broker"
            Component = "BrokerExecutableComponent"
        },
        [pscustomobject]@{
            Feature = "Broker"
            Component = "BrokerDataDirectoryComponent"
        }
    )) {
        if (-not ($FeatureComponents | Where-Object {
            $_.Values[0] -ceq $ExpectedMapping.Feature -and
            $_.Values[1] -ceq $ExpectedMapping.Component
        })) {
            throw (
                "installer_contract_feature_component_missing:" +
                $ExpectedMapping.Feature +
                ":" +
                $ExpectedMapping.Component
            )
        }
    }

    [ordered]@{
        schemaVersion = "1.0"
        passed = $true
        productCode = $Properties.ProductCode
        productVersion = $Properties.ProductVersion
        manufacturer = $Properties.Manufacturer
        featureLevels = [ordered]@{
            Core = $Features.Core
            Broker = $Features.Broker
        }
        service = "PerfMonitorBroker"
        fileCount = $FileIds.Count
        msiSha256 = Get-PerfMonitorSha256 -Path $MsiPath
        signatureMode = $SignatureMode
    } | ConvertTo-Json -Depth 6
}
finally {
    if ($null -ne $Opened.Database) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $Opened.Database
        ) | Out-Null
    }
    if ($null -ne $Opened.Installer) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $Opened.Installer
        ) | Out-Null
    }
}
