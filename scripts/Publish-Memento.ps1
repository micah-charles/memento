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
$hashPath = Join-Path $OutputRoot "MEMENTO-win-x64.zip.sha256"

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path $hashPath) { Remove-Item -LiteralPath $hashPath -Force }

dotnet publish (Join-Path $repoRoot 'src\Memento.App\Memento.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    /p:WindowsAppSDKSelfContained=true

# The bundle is intentionally an installer-ready zip, not a signed MSIX.
# A signed package still needs an owner-selected publisher identity and certificate.
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash *MEMENTO-win-x64.zip" -Encoding ascii
Write-Output "Published self-contained bundle: $zipPath"
Write-Output "SHA-256 sidecar: $hashPath"
