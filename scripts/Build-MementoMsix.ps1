[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',
    [Parameter(Mandatory = $true)]
    [string]$Publisher,
    [string]$PackageName = 'MicahCharles.Memento',
    [string]$CertificatePath = '',
    [string]$CertificatePassword = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$MakeAppxPath = '',
    [string]$SignToolPath = '',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'artifacts\msix' }
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$templatePath = Join-Path $repoRoot 'packaging\Package.appxmanifest.template.xml'

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
        throw "$ToolName was not found. Install the Windows 10/11 SDK or pass -$($ToolName.Replace('.exe', 'Path'))."
    }
    return $candidate.FullName
}

function Write-PlaceholderPng {
    param([Parameter(Mandatory = $true)][string]$Path)
    # The packaging script deliberately generates deterministic placeholders;
    # replace these with reviewed product artwork before distribution.
    $bytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { throw "Package manifest template not found: $templatePath" }
$makeappx = Resolve-SdkTool -ToolName 'makeappx.exe' -ExplicitPath $MakeAppxPath
$signtool = $null
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw "Certificate not found: $CertificatePath" }
    $signtool = Resolve-SdkTool -ToolName 'signtool.exe' -ExplicitPath $SignToolPath
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('memento-msix-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging 'publish'
$assets = Join-Path $staging 'Assets'
$manifest = Join-Path $staging 'AppxManifest.xml'
$unsignedPackage = Join-Path $OutputRoot "MEMENTO-$Version-unsigned.msix"
$signedPackage = Join-Path $OutputRoot "MEMENTO-$Version.msix"

try {
    New-Item -ItemType Directory -Force -Path $staging, $assets | Out-Null
    dotnet publish (Join-Path $repoRoot 'src\Memento.App\Memento.App.csproj') `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publish `
        /p:WindowsPackageType=None `
        /p:WindowsAppSDKSelfContained=true

    Copy-Item -Path (Join-Path $publish '*') -Destination $staging -Recurse -Force
    # The publish directory is only an intermediate copy source. Leaving it
    # under the package root would duplicate every payload file in the MSIX.
    Remove-Item -LiteralPath $publish -Recurse -Force
    $stagedExecutables = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter 'Memento.App.exe')
    if ($stagedExecutables.Count -ne 1 -or $stagedExecutables[0].FullName -ne (Join-Path $staging 'Memento.App.exe')) {
        throw "MSIX staging must contain exactly one root Memento.App.exe; found $($stagedExecutables.Count)."
    }
    foreach ($name in 'StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png', 'SplashScreen.png') {
        Write-PlaceholderPng -Path (Join-Path $assets $name)
    }
    $manifestText = Get-Content -LiteralPath $templatePath -Raw
    $escapedPublisher = [System.Security.SecurityElement]::Escape($Publisher)
    $manifestText = $manifestText.Replace('__PACKAGE_NAME__', $PackageName).Replace('__PUBLISHER__', $escapedPublisher).Replace('__VERSION__', $Version)
    Set-Content -LiteralPath $manifest -Value $manifestText -Encoding utf8

    if (Test-Path -LiteralPath $unsignedPackage) { Remove-Item -LiteralPath $unsignedPackage -Force }
    & $makeappx pack /d $staging /p $unsignedPackage /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE." }

    if ($null -eq $signtool) {
        Write-Warning "Created an unsigned MSIX. It is not installable for ordinary users until signed with a certificate whose subject matches the manifest Publisher ($Publisher)."
        Write-Output "Unsigned MSIX: $unsignedPackage"
        return
    }

    if (Test-Path -LiteralPath $signedPackage) { Remove-Item -LiteralPath $signedPackage -Force }
    $signArguments = @('sign', '/fd', 'SHA256', '/f', $CertificatePath)
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) { $signArguments += @('/p', $CertificatePassword) }
    $signArguments += @('/a')
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $signArguments += $unsignedPackage
    & $signtool @signArguments
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    Move-Item -LiteralPath $unsignedPackage -Destination $signedPackage -Force
    Write-Output "Signed MSIX: $signedPackage"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
