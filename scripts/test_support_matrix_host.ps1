[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "windows-11-24h2",
        "windows-11-25h2",
        "windows-server-2022-desktop",
        "windows-server-2025-desktop"
    )]
    [string]$TargetId
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release_common.ps1")

[ordered]@{
    schemaVersion = "1.0"
    targetId = $TargetId
    host = Get-PerfMonitorSupportMatrixHostEvidence `
        -TargetId $TargetId
    passed = $true
} | ConvertTo-Json -Depth 6
