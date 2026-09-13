[CmdletBinding()]
param(
    [string]$BundlePath = '',
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ([string]::IsNullOrWhiteSpace($BundlePath)) { $BundlePath = Join-Path $repoRoot 'artifacts\MEMENTO-win-x64.zip' }
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path $env:LOCALAPPDATA 'MEMENTO\App' }
$BundlePath = [System.IO.Path]::GetFullPath($BundlePath)
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)

if (-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)) {
    throw "Bundle not found: $BundlePath. Run scripts\Publish-Memento.ps1 first."
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('memento-install-' + [Guid]::NewGuid().ToString('N'))
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO'
$shortcutPath = Join-Path $shortcutDirectory 'MEMENTO.lnk'
try {
    Expand-Archive -LiteralPath $BundlePath -DestinationPath $temporaryRoot -Force
    $executable = Join-Path $temporaryRoot 'Memento.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The bundle does not contain Memento.App.exe.' }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Copy-Item -Path (Join-Path $temporaryRoot '*') -Destination $InstallRoot -Recurse -Force

    New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $InstallRoot 'Memento.App.exe'
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Description = 'MEMENTO local family archive'
    $shortcut.Save()

    Write-Output "Installed MEMENTO to $InstallRoot"
    Write-Output "Start Menu shortcut: $shortcutPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
