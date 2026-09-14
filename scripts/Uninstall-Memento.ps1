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
if (Test-Path -LiteralPath $shortcutDirectory -PathType Container -and -not (Get-ChildItem -LiteralPath $shortcutDirectory -Force)) {
    Remove-Item -LiteralPath $shortcutDirectory -Force
}
if (Test-Path -LiteralPath $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}
if (Test-Path -LiteralPath $uninstallRegistryPath) {
    Remove-Item -LiteralPath $uninstallRegistryPath -Recurse -Force
}

if ($RemoveData) {
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
    Write-Output "Removed MEMENTO app and local data: $dataRoot"
}
else {
    Write-Output "Removed MEMENTO app. Local data was preserved: $dataRoot"
    Write-Output 'Use -RemoveData only after confirming the archive is backed up and no longer needed.'
}
