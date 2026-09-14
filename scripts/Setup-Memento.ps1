[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$RequireCloudCredential,
    [switch]$RequireApplicationLock,
    [switch]$RequireAudioInput,
    [switch]$RequireAudioOutput,
    [switch]$RequireArchiveIntegrity,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function Invoke-MementoStep([string]$ScriptName, [hashtable]$Parameters = @{}) {
    $path = Join-Path $PSScriptRoot $ScriptName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "MEMENTO setup script was not found: $path"
    }
    Write-Output "Running $ScriptName"
    # PowerShell scripts do not necessarily assign LASTEXITCODE. Reset it so
    # a prior native command cannot make a successful step look like a failure.
    $global:LASTEXITCODE = 0
    & $path @Parameters
    if ($LASTEXITCODE -ne 0) {
        throw "$ScriptName failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipPublish) {
    Invoke-MementoStep 'Publish-Memento.ps1' @{ Configuration = $Configuration }
}

Invoke-MementoStep 'Install-Memento.ps1'

$preflightParameters = @{}
if ($RequireCloudCredential) { $preflightParameters.RequireCloudCredential = $true }
if ($RequireApplicationLock) { $preflightParameters.RequireApplicationLock = $true }
if ($RequireAudioInput) { $preflightParameters.RequireAudioInput = $true }
if ($RequireAudioOutput) { $preflightParameters.RequireAudioOutput = $true }
if ($RequireArchiveIntegrity) { $preflightParameters.RequireArchiveIntegrity = $true }
Invoke-MementoStep 'Test-MementoPreflight.ps1' $preflightParameters

if ($Launch) {
    Invoke-MementoStep 'Start-Memento.ps1'
}

Write-Output 'MEMENTO setup completed.'
