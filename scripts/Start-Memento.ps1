[CmdletBinding()]
param(
    [string]$ExecutablePath = ''
)

$ErrorActionPreference = 'Stop'
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$installedExecutable = Join-Path $localAppData 'MEMENTO\App\Memento.App.exe'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$publishedExecutable = Join-Path $repoRoot 'artifacts\publish\win-x64\Memento.App.exe'

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
        $ExecutablePath = $installedExecutable
    }
    elseif (Test-Path -LiteralPath $publishedExecutable -PathType Leaf) {
        $ExecutablePath = $publishedExecutable
    }
    else {
        throw "MEMENTO executable was not found. Install the bundle with scripts\Install-Memento.ps1 or publish it with scripts\Publish-Memento.ps1."
    }
}

$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "MEMENTO executable was not found: $ExecutablePath"
}

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    Write-Output 'MEMENTO is already running.'
    return
}

$workingDirectory = Split-Path -Parent $ExecutablePath
Start-Process -FilePath $ExecutablePath -WorkingDirectory $workingDirectory | Out-Null
Write-Output "Started MEMENTO: $ExecutablePath"
