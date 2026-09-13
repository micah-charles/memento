[CmdletBinding()]
param(
    [string]$BundlePath = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [long]$MinimumFreeBytes = 1073741824
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($BundlePath)) { $BundlePath = Join-Path $repoRoot 'artifacts\MEMENTO-win-x64.zip' }
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path $localAppData 'MEMENTO\App' }
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $localAppData 'MEMENTO' }
$BundlePath = [System.IO.Path]::GetFullPath($BundlePath)
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)

$failures = 0
function Write-Check([string]$Name, [bool]$Passed, [string]$Detail, [string]$Severity = 'FAIL') {
    if ($Passed) {
        Write-Output ("PASS  {0}: {1}" -f $Name, $Detail)
        return
    }

    Write-Output ("{0}  {1}: {2}" -f $Severity, $Name, $Detail)
    if ($Severity -eq 'FAIL') { $script:failures++ }
}

$bundleExists = Test-Path -LiteralPath $BundlePath -PathType Leaf
Write-Check 'bundle' $bundleExists ($(if ($bundleExists) { $BundlePath } else { "not found at $BundlePath" }))
if ($bundleExists) {
    $sidecarPath = "$BundlePath.sha256"
    $sidecarExists = Test-Path -LiteralPath $sidecarPath -PathType Leaf
    Write-Check 'bundle checksum sidecar' $sidecarExists ($(if ($sidecarExists) { $sidecarPath } else { "not found at $sidecarPath" }))
    if ($sidecarExists) {
        $expected = (Get-Content -LiteralPath $sidecarPath -Raw).Trim().Split()[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $BundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Check 'bundle checksum' ($expected -match '^[0-9a-f]{64}$' -and $expected -eq $actual) "SHA-256 $actual"
    }
}

$installedExecutable = Join-Path $InstallRoot 'Memento.App.exe'
$installedExists = Test-Path -LiteralPath $installedExecutable -PathType Leaf
Write-Check 'installed executable' $installedExists ($(if ($installedExists) { $installedExecutable } else { "not found at $installedExecutable" }))

$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO\MEMENTO.lnk'
Write-Check 'Start Menu shortcut' (Test-Path -LiteralPath $shortcutPath -PathType Leaf) $shortcutPath 'WARN'

$dataRootExists = Test-Path -LiteralPath $DataRoot -PathType Container
Write-Check 'archive directory' $dataRootExists ($(if ($dataRootExists) { $DataRoot } else { "not initialized at $DataRoot" })) 'WARN'
$databasePath = Join-Path $DataRoot 'data\memory.db'
Write-Check 'archive database' (Test-Path -LiteralPath $databasePath -PathType Leaf) ($(if (Test-Path -LiteralPath $databasePath -PathType Leaf) { $databasePath } else { 'created on first launch' })) 'WARN'

try {
    $driveRoot = [System.IO.Path]::GetPathRoot($DataRoot)
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    $freeBytes = $drive.AvailableFreeSpace
    Write-Check 'free disk space' ($freeBytes -ge $MinimumFreeBytes) ("{0:N0} bytes available; minimum {1:N0}" -f $freeBytes, $MinimumFreeBytes)
}
catch {
    Write-Check 'free disk space' $false $_.Exception.GetType().Name
}

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
Write-Check 'MEMENTO process state' ($null -eq $running) ($(if ($null -eq $running) { 'not running; safe for install/update' } else { 'running; close before install/update' })) 'WARN'

Write-Output 'INFO  native GUI, microphone hardware, live provider credentials, and participant UX still require supervised target-machine verification.'
if ($failures -gt 0) {
    Write-Output "Preflight failed: $failures blocking check(s)."
    exit 1
}

Write-Output 'Preflight passed: no blocking deployment checks failed.'
