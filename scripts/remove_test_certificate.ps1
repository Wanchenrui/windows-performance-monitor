[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{40}$")]
    [string]$Sha1Thumbprint
)

$ErrorActionPreference = "Stop"
$Normalized = $Sha1Thumbprint.ToUpperInvariant()
foreach ($Store in @(
    "Cert:\CurrentUser\My",
    "Cert:\CurrentUser\Root",
    "Cert:\CurrentUser\TrustedPublisher"
)) {
    $Matches = @(
        Get-ChildItem -LiteralPath $Store |
            Where-Object {
                $_.Thumbprint.ToUpperInvariant() -eq $Normalized
            }
    )
    foreach ($Certificate in $Matches) {
        if (
            $Certificate.Subject -notlike (
                "CN=PerfMonitor CI Test*"
            )
        ) {
            throw "refusing_to_remove_non_test_certificate"
        }
        Remove-Item -LiteralPath $Certificate.PSPath -Force
    }
}
