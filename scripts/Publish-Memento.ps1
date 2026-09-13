[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $PSScriptRoot '..\artifacts' }
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = Join-Path $OutputRoot "publish\win-x64"
$zipPath = Join-Path $OutputRoot "MEMENTO-win-x64.zip"

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

dotnet publish (Join-Path $repoRoot 'src\Memento.App\Memento.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    /p:WindowsAppSDKSelfContained=true

# The bundle is intentionally an installer-ready zip, not a signed MSIX.
# A signed package still needs an owner-selected publisher identity and certificate.
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
Write-Output "Published self-contained bundle: $zipPath"
