[CmdletBinding()]
param(
    [string]$InstallRoot = '',
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$defaultInstallRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'MEMENTO\App'))
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = $defaultInstallRoot }
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)

if (-not [string]::Equals($InstallRoot, $defaultInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallRoot must be the per-user MEMENTO app directory: $defaultInstallRoot"
}

$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'MEMENTO'))
$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO\MEMENTO.lnk'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MEMENTO'
$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw 'MEMENTO is still running. Close it before uninstalling.'
}

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
$shortcutDirectory = Split-Path -Parent $shortcutPath
if ((Test-Path -LiteralPath $shortcutDirectory -PathType Container) -and -not (Get-ChildItem -LiteralPath $shortcutDirectory -Force)) {
    Remove-Item -LiteralPath $shortcutDirectory -Force
}
if (Test-Path -LiteralPath $uninstallRegistryPath) {
    Remove-Item -LiteralPath $uninstallRegistryPath -Recurse -Force
}

# When this script is launched from the installed app tree, PowerShell keeps
# the script file open until the process exits. Schedule deletion from a
# short-lived helper outside that tree so Installed apps uninstallation can
# remove the complete directory, including this script itself.
$cleanupScript = Join-Path ([System.IO.Path]::GetTempPath()) ('memento-uninstall-' + [Guid]::NewGuid().ToString('N') + '.cmd')
$cleanupLines = [System.Collections.Generic.List[string]]::new()
$cleanupLines.Add('@echo off')
$cleanupLines.Add('ping 127.0.0.1 -n 2 >nul')
$cleanupLines.Add('rmdir /s /q "' + $InstallRoot + '"')
if ($RemoveData) {
    $cleanupLines.Add('rmdir /s /q "' + $dataRoot + '"')
}
$cleanupLines.Add('del /f /q "%~f0" >nul 2>&1')
Set-Content -LiteralPath $cleanupScript -Value $cleanupLines -Encoding ASCII
try {
    Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/c', '"' + $cleanupScript + '"') -WindowStyle Hidden | Out-Null
}
catch {
    if (Test-Path -LiteralPath $cleanupScript) { Remove-Item -LiteralPath $cleanupScript -Force -ErrorAction SilentlyContinue }
    throw
}

if ($RemoveData) {
    Write-Output "Scheduled removal of MEMENTO app and local data: $dataRoot"
}
else {
    Write-Output "Scheduled removal of MEMENTO app. Local data will be preserved: $dataRoot"
    Write-Output 'Use -RemoveData only after confirming the archive is backed up and no longer needed.'
}
