[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TargetName = 'MEMENTO/AppLock'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($TargetName)) {
    throw 'A Credential Manager target name is required.'
}
if (-not [string]::Equals($TargetName, 'MEMENTO/AppLock', [StringComparison]::Ordinal)) {
    throw "This recovery helper can only remove the MEMENTO/AppLock credential."
}

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw 'MEMENTO is still running. Close it before resetting the application lock.'
}

# cmdkey lists/deletes credential metadata only; it never prints the secret.
$credentialListing = (& cmdkey.exe "/list:$TargetName" 2>$null | Out-String)
$credentialConfigured = -not [string]::IsNullOrWhiteSpace($credentialListing) -and $credentialListing -notmatch '\*\s*NONE\s*\*'
if (-not $credentialConfigured) {
    Write-Output "No application-lock credential was found for '$TargetName'. Archive data was not changed."
    return
}

if ($PSCmdlet.ShouldProcess($TargetName, 'Delete application-lock credential from the current Windows user')) {
    & cmdkey.exe "/delete:$TargetName" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Windows Credential Manager could not remove '$TargetName' (exit code $LASTEXITCODE)."
    }
    Write-Output "Removed application-lock credential '$TargetName'. Archive data was not changed."
}
