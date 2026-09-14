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

function Read-PngDimensions {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    $validSignature = $bytes.Length -ge 24
    for ($index = 0; $validSignature -and $index -lt $signature.Length; $index++) {
        if ($bytes[$index] -ne $signature[$index]) { $validSignature = $false }
    }
    if (-not $validSignature) {
        throw "PNG asset is invalid or truncated: $Path"
    }
    $width = ([uint32]$bytes[16] -shl 24) -bor ([uint32]$bytes[17] -shl 16) -bor ([uint32]$bytes[18] -shl 8) -bor [uint32]$bytes[19]
    $height = ([uint32]$bytes[20] -shl 24) -bor ([uint32]$bytes[21] -shl 16) -bor ([uint32]$bytes[22] -shl 8) -bor [uint32]$bytes[23]
    if ($width -eq 0 -or $height -eq 0) { throw "PNG asset has an empty dimension: $Path" }
    return [pscustomobject]@{ Width = $width; Height = $height }
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
    try {
        $manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    }
    catch { throw "MSIX manifest is not valid XML: $($_.Exception.Message)" }

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaceManager.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
    if ($null -eq $identity -or [string]::IsNullOrWhiteSpace($identity.Name) -or [string]::IsNullOrWhiteSpace($identity.Publisher) -or [string]::IsNullOrWhiteSpace($identity.Version)) {
        throw 'MSIX manifest has an incomplete package identity.'
    }
    if ($identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "MSIX manifest has an invalid package version: $($identity.Version)"
    }

    $application = $manifest.SelectSingleNode('/appx:Package/appx:Applications/appx:Application', $namespaceManager)
    if ($null -eq $application -or $application.Id -ne 'Memento' -or $application.Executable -ne 'Memento.App.exe' -or $application.EntryPoint -ne 'Windows.FullTrustApplication') {
        throw 'MSIX manifest does not describe the expected MEMENTO application entry.'
    }
    $visualElements = $null
    if ($null -ne $application) {
        $visualElements = $application.SelectSingleNode('uap:VisualElements', $namespaceManager)
    }
    $splashScreen = $null
    if ($null -ne $visualElements) {
        $splashScreen = $visualElements.SelectSingleNode('uap:SplashScreen', $namespaceManager)
    }
    if ($null -eq $visualElements -or $visualElements.DisplayName -ne 'MEMENTO' -or $visualElements.Square150x150Logo -ne 'Assets\Square150x150Logo.png' -or $visualElements.Square44x44Logo -ne 'Assets\Square44x44Logo.png' -or $null -eq $splashScreen -or $splashScreen.Image -ne 'Assets\SplashScreen.png') {
        throw 'MSIX manifest does not describe the expected MEMENTO visual assets.'
    }

    $executables = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter 'Memento.App.exe')
    if ($executables.Count -ne 1 -or $executables[0].FullName -ne (Join-Path $staging 'Memento.App.exe')) {
        throw "MSIX must contain exactly one root Memento.App.exe; found $($executables.Count)."
    }
    if (Test-Path -LiteralPath (Join-Path $staging 'publish')) { throw 'MSIX contains an unexpected nested publish directory.' }
    $assetsDirectory = Join-Path $staging 'Assets'
    $assets = @(Get-ChildItem -LiteralPath $assetsDirectory -File -ErrorAction SilentlyContinue)
    $expectedAssetNames = @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png', 'SplashScreen.png')
    $expectedAssetDimensions = @{
        'StoreLogo.png' = '256x256'
        'Square150x150Logo.png' = '150x150'
        'Square44x44Logo.png' = '44x44'
        'SplashScreen.png' = '620x300'
    }
    $actualAssetNames = @($assets | ForEach-Object { $_.Name })
    $missingAssets = @($expectedAssetNames | Where-Object { $_ -notin $actualAssetNames })
    $unexpectedAssets = @($actualAssetNames | Where-Object { $_ -notin $expectedAssetNames })
    if ($missingAssets.Count -gt 0 -or $unexpectedAssets.Count -gt 0) {
        throw "MSIX package assets do not match the expected set. Missing: $($missingAssets -join ', '); unexpected: $($unexpectedAssets -join ', ')"
    }
    foreach ($assetName in $expectedAssetNames) {
        $assetPath = Join-Path $assetsDirectory $assetName
        $dimensions = Read-PngDimensions -Path $assetPath
        $actualDimensions = "{0}x{1}" -f $dimensions.Width, $dimensions.Height
        if ($actualDimensions -ne $expectedAssetDimensions[$assetName]) {
            throw "MSIX asset $assetName has dimensions $actualDimensions; expected $($expectedAssetDimensions[$assetName])."
        }
    }

    if ($null -ne $signtool) {
        & $signtool verify /pa $PackagePath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool verification failed with exit code $LASTEXITCODE." }
    }

    Write-Output "PASS  package: $PackagePath"
    Write-Output "PASS  manifest: identity $($identity.Name), version $($identity.Version), expected MEMENTO application entry"
    Write-Output 'PASS  payload: one root Memento.App.exe; no nested publish directory'
    Write-Output 'PASS  assets: expected files present with approved PNG dimensions'
    if ($null -ne $signtool) { Write-Output 'PASS  signature: signtool /pa verification' }
    else { Write-Output 'WARN  signature: package was not signature-verified' }
    Write-Output "MSIX verification passed: $PackagePath"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
