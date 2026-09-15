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
$manifestName = 'MEMENTO.payload-manifest.json'

function Assert-ChildPath([string]$Child, [string]$Parent, [string]$Description) {
    $childFull = [System.IO.Path]::GetFullPath($Child)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $childFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $childFull -eq $parentFull) {
        throw "$Description is outside its intended parent: $childFull"
    }
    return $childFull
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Assert-ChildPath $publishDirectory $OutputRoot 'Publish directory' | Out-Null
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path $hashPath) { Remove-Item -LiteralPath $hashPath -Force }

dotnet publish (Join-Path $repoRoot 'src\Memento.App\Memento.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    /p:WindowsAppSDKSelfContained=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$sourceCommit = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = 'unknown' }
$sourceDirty = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain 2>$null | Out-String).Trim())
$files = @(
    Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Where-Object { $_.Name -ne $manifestName } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($publishDirectory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).Replace('\', '/')
            [pscustomobject]@{
                path = $relative
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$required = @('Memento.App.dll', 'Memento.Core.dll')
$resourceFiles = @($files | Where-Object { $_.path -match '(?i)\.pri$' })
if ($resourceFiles.Count -gt 0) { $required += @($resourceFiles.path) }
foreach ($requiredFile in $required) {
    if (-not ($files.path -contains $requiredFile)) { throw "Published payload is missing required file: $requiredFile" }
}
$manifest = [pscustomobject]@{
    schema_version = 1
    source_commit = $sourceCommit
    source_dirty = $sourceDirty
    configuration = $Configuration
    runtime = 'win-x64'
    required = $required
    files = $files
}
$manifestPath = Join-Path $publishDirectory $manifestName
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Output "Payload manifest: $manifestPath"

# The bundle is intentionally an installer-ready zip, not a signed MSIX.
# A signed package still needs an owner-selected publisher identity and certificate.
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash *MEMENTO-win-x64.zip" -Encoding ascii
Write-Output "Published self-contained bundle: $zipPath"
Write-Output "SHA-256 sidecar: $hashPath"
