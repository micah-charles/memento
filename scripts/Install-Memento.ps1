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

function Assert-NoReparsePointInPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description cannot contain a reparse point: $current"
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = $parent
    }
}

Assert-NoReparsePointInPath -Path $InstallRoot -Description 'The install path'

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
$installParent = [System.IO.Path]::GetFullPath($installParent)
Assert-NoReparsePointInPath -Path $installParent -Description 'The install parent path'
$stagingRoot = Join-Path $installParent ('.App-staging-' + [Guid]::NewGuid().ToString('N'))
$previousRoot = Join-Path $installParent ('.App-previous-' + [Guid]::NewGuid().ToString('N'))
$failedRoot = Join-Path $installParent ('.App-failed-' + [Guid]::NewGuid().ToString('N'))
$swapped = $false
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO'
$shortcutPath = Join-Path $shortcutDirectory 'MEMENTO.lnk'
$uninstallScriptSource = Join-Path $repoRoot 'scripts\Uninstall-Memento.ps1'
$recoveryScriptSource = Join-Path $repoRoot 'scripts\Reset-MementoApplicationLock.ps1'
$credentialScriptSource = Join-Path $repoRoot 'scripts\Set-MementoOpenAiCredential.ps1'
$credentialRemovalScriptSource = Join-Path $repoRoot 'scripts\Remove-MementoOpenAiCredential.ps1'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MEMENTO'

function Move-DirectoryWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$Attempts = 5
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Move-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds (250 * $attempt)
            }
        }
    }

    throw $lastError
}

try {
    New-Item -ItemType Directory -Force -Path $installParent | Out-Null
    Expand-Archive -LiteralPath $BundlePath -DestinationPath $temporaryRoot -Force
    $executable = Join-Path $temporaryRoot 'Memento.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The bundle does not contain Memento.App.exe.' }

    # Build a clean staging tree so removed files from an older bundle cannot
    # survive an update. Keep it beside the install root so the final moves do
    # not cross volumes. The archive lives in the parent MEMENTO directory and
    # is deliberately outside this tree.
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Assert-NoReparsePointInPath -Path $stagingRoot -Description 'The install staging path'
    Copy-Item -Path (Join-Path $temporaryRoot '*') -Destination $stagingRoot -Recurse -Force
    if (-not (Test-Path -LiteralPath $uninstallScriptSource -PathType Leaf)) {
        throw "Uninstall script not found: $uninstallScriptSource"
    }
    Copy-Item -LiteralPath $uninstallScriptSource -Destination (Join-Path $stagingRoot 'Uninstall-Memento.ps1') -Force
    if (-not (Test-Path -LiteralPath $recoveryScriptSource -PathType Leaf)) {
        throw "Application-lock recovery script not found: $recoveryScriptSource"
    }
    Copy-Item -LiteralPath $recoveryScriptSource -Destination (Join-Path $stagingRoot 'Reset-MementoApplicationLock.ps1') -Force
    if (-not (Test-Path -LiteralPath $credentialScriptSource -PathType Leaf)) {
        throw "OpenAI credential setup script not found: $credentialScriptSource"
    }
    Copy-Item -LiteralPath $credentialScriptSource -Destination (Join-Path $stagingRoot 'Set-MementoOpenAiCredential.ps1') -Force
    if (-not (Test-Path -LiteralPath $credentialRemovalScriptSource -PathType Leaf)) {
        throw "OpenAI credential removal script not found: $credentialRemovalScriptSource"
    }
    Copy-Item -LiteralPath $credentialRemovalScriptSource -Destination (Join-Path $stagingRoot 'Remove-MementoOpenAiCredential.ps1') -Force
    if (Test-Path -LiteralPath $InstallRoot) {
        Move-DirectoryWithRetry -Source $InstallRoot -Destination $previousRoot
    }
    Move-DirectoryWithRetry -Source $stagingRoot -Destination $InstallRoot
    $swapped = $true

    New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $InstallRoot 'Memento.App.exe'
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Description = 'MEMENTO local family archive'
    $shortcut.Save()

    # Register the per-user installation so Windows Settings can offer an
    # ordinary uninstall entry without requiring elevation. The registered
    # script preserves the archive unless the user explicitly requests data
    # removal.
    $uninstallScript = Join-Path $InstallRoot 'Uninstall-Memento.ps1'
    $uninstallCommand = 'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $uninstallScript + '"'
    New-Item -Path $uninstallRegistryPath -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'DisplayName' -Value 'MEMENTO' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'DisplayVersion' -Value '0.1.0' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'Publisher' -Value 'MEMENTO' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'InstallLocation' -Value $InstallRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'DisplayIcon' -Value ((Join-Path $InstallRoot 'Memento.App.exe') + ',0') -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'UninstallString' -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'QuietUninstallString' -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

    Write-Output "Installed MEMENTO to $InstallRoot"
    Write-Output "Start Menu shortcut: $shortcutPath"
    Write-Output 'Windows Installed apps registration: MEMENTO (per-user)'
}
catch {
    $failure = $_
    if ($swapped) {
        # A post-swap failure (for example, Start Menu shortcut creation) must
        # leave the previous install available at the original path. The
        # shortcut also targets that stable path, so restoring the tree keeps
        # an existing shortcut usable.
        if (Test-Path -LiteralPath $InstallRoot) {
            Move-DirectoryWithRetry -Source $InstallRoot -Destination $failedRoot
        }
        $swapped = $false
    }
    if ((Test-Path -LiteralPath $previousRoot) -and -not (Test-Path -LiteralPath $InstallRoot)) {
        Move-DirectoryWithRetry -Source $previousRoot -Destination $InstallRoot
    }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
    if ($swapped -and (Test-Path -LiteralPath $previousRoot)) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }
    if (Test-Path -LiteralPath $failedRoot) { Remove-Item -LiteralPath $failedRoot -Recurse -Force }
}
