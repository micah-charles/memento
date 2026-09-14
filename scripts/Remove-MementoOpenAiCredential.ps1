[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
$targetName = 'MEMENTO/OpenAI'

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw 'MEMENTO is still running. Close it before removing the cloud credential.'
}

# cmdkey lists only credential metadata; it never prints the secret.
$credentialListing = (& cmdkey.exe "/list:$targetName" 2>$null | Out-String)
$credentialConfigured = -not [string]::IsNullOrWhiteSpace($credentialListing) -and $credentialListing -notmatch '\*\s*NONE\s*\*'
if (-not $credentialConfigured) {
    Write-Output "No cloud credential was found for '$targetName'."
    return
}

if ($PSCmdlet.ShouldProcess($targetName, 'Delete the current Windows user cloud credential')) {
    & cmdkey.exe "/delete:$targetName" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Windows Credential Manager could not remove '$targetName' (exit code $LASTEXITCODE)."
    }
    Write-Output "Removed '$targetName' from the current Windows user's Credential Manager."
}
