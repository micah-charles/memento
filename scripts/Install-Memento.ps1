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

$hashPath = "$BundlePath.sha256"
if (Test-Path -LiteralPath $hashPath -PathType Leaf) {
    $expectedHash = (Get-Content -LiteralPath $hashPath -Raw).Trim().Split()[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $BundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or $expectedHash -ne $actualHash) {
        throw "Bundle SHA-256 verification failed: $BundlePath"
    }
    Write-Output "Verified bundle SHA-256: $actualHash"
}

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw 'MEMENTO is still running. Close it before installing an update.'
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('memento-install-' + [Guid]::NewGuid().ToString('N'))
$installParent = Split-Path -Parent $InstallRoot
$stagingRoot = Join-Path $installParent ('.App-staging-' + [Guid]::NewGuid().ToString('N'))
$previousRoot = Join-Path $installParent ('.App-previous-' + [Guid]::NewGuid().ToString('N'))
$swapped = $false
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO'
$shortcutPath = Join-Path $shortcutDirectory 'MEMENTO.lnk'
try {
    Expand-Archive -LiteralPath $BundlePath -DestinationPath $temporaryRoot -Force
    $executable = Join-Path $temporaryRoot 'Memento.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The bundle does not contain Memento.App.exe.' }

    # Build a clean staging tree so removed files from an older bundle cannot
    # survive an update. Keep it beside the install root so the final moves do
    # not cross volumes. The archive lives in the parent MEMENTO directory and
    # is deliberately outside this tree.
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Copy-Item -Path (Join-Path $temporaryRoot '*') -Destination $stagingRoot -Recurse -Force
    if (Test-Path -LiteralPath $InstallRoot) {
        Move-Item -LiteralPath $InstallRoot -Destination $previousRoot
    }
    Move-Item -LiteralPath $stagingRoot -Destination $InstallRoot
    $swapped = $true

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
catch {
    if (-not $swapped -and (Test-Path -LiteralPath $previousRoot) -and -not (Test-Path -LiteralPath $InstallRoot)) {
        Move-Item -LiteralPath $previousRoot -Destination $InstallRoot
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
    if ($swapped -and (Test-Path -LiteralPath $previousRoot)) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }
}
