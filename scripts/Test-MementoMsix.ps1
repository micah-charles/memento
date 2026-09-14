[CmdletBinding()]
param(
    [string]$PackagePath = '',
    [string]$MakeAppxPath = '',
    [string]$SignToolPath = '',
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PackagePath = Join-Path $repoRoot 'artifacts\msix\MEMENTO-0.1.0.0-unsigned.msix' }
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)

function Resolve-SdkTool {
    param([Parameter(Mandatory = $true)][string]$ToolName, [string]$ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = (Resolve-Path -LiteralPath $ExplicitPath -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Tool not found: $resolved" }
        return $resolved
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter $ToolName -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '\\x64$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        $userProfile = [Environment]::GetFolderPath('UserProfile')
        $buildToolsRoot = Join-Path $userProfile '.nuget\packages\microsoft.windows.sdk.buildtools'
        $candidate = Get-ChildItem -LiteralPath $buildToolsRoot -Recurse -Filter $ToolName -File -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match '\\x64$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
    if ($null -eq $candidate) {
        throw "$ToolName was not found. Install the Windows 10/11 SDK or Microsoft.Windows.SDK.BuildTools, or pass an explicit path."
    }
    return $candidate.FullName
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "MSIX package not found: $PackagePath"
}

$makeappx = Resolve-SdkTool -ToolName 'makeappx.exe' -ExplicitPath $MakeAppxPath
$signtool = $null
if ($RequireSignature -or -not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    $signtool = Resolve-SdkTool -ToolName 'signtool.exe' -ExplicitPath $SignToolPath
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('memento-msix-verify-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    & $makeappx unpack /p $PackagePath /d $staging /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed with exit code $LASTEXITCODE." }

    $manifestPath = Join-Path $staging 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'MSIX is missing AppxManifest.xml.' }
    try { [xml](Get-Content -LiteralPath $manifestPath -Raw) | Out-Null }
    catch { throw "MSIX manifest is not valid XML: $($_.Exception.Message)" }

    $executables = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter 'Memento.App.exe')
    if ($executables.Count -ne 1 -or $executables[0].FullName -ne (Join-Path $staging 'Memento.App.exe')) {
        throw "MSIX must contain exactly one root Memento.App.exe; found $($executables.Count)."
    }
    if (Test-Path -LiteralPath (Join-Path $staging 'publish')) { throw 'MSIX contains an unexpected nested publish directory.' }
    $assetsDirectory = Join-Path $staging 'Assets'
    $assets = @(Get-ChildItem -LiteralPath $assetsDirectory -File -ErrorAction SilentlyContinue)
    if ($assets.Count -ne 4) { throw "MSIX must contain exactly four package assets; found $($assets.Count)." }

    if ($null -ne $signtool) {
        & $signtool verify /pa $PackagePath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool verification failed with exit code $LASTEXITCODE." }
    }

    Write-Output "PASS  package: $PackagePath"
    Write-Output "PASS  manifest: parseable AppxManifest.xml"
    Write-Output 'PASS  payload: one root Memento.App.exe; no nested publish directory'
    Write-Output 'PASS  assets: four package assets'
    if ($null -ne $signtool) { Write-Output 'PASS  signature: signtool /pa verification' }
    else { Write-Output 'WARN  signature: package was not signature-verified' }
    Write-Output "MSIX verification passed: $PackagePath"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
