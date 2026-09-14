[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipDotnet,
    [switch]$SkipLanguageValidation,
    [switch]$SkipPreflight,
    [switch]$VerifyMsix,
    [switch]$RequireMsixSignature,
    [string]$MsixPackagePath = '',
    [switch]$RequireCloudCredential,
    [switch]$RequireApplicationLock,
    [switch]$RequireAudioInput,
    [switch]$RequireAudioOutput,
    [switch]$RequireArchiveIntegrity
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ($RequireMsixSignature -or -not [string]::IsNullOrWhiteSpace($MsixPackagePath)) {
    $VerifyMsix = $true
}

function Invoke-NativeStep([string]$FilePath, [string[]]$Arguments) {
    Write-Output ("Running {0} {1}" -f $FilePath, ($Arguments -join ' '))
    $global:LASTEXITCODE = 0
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath"
    }
}

function Test-PowerShellScripts {
    $errors = [System.Collections.Generic.List[string]]::new()
    $count = 0
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts') -Filter '*.ps1' -File | ForEach-Object {
        $count++
        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        foreach ($parseError in $parseErrors) {
            $errors.Add("$($_.Name): $($parseError.Message)")
        }
    }
    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Error $_ }
        throw "PowerShell parser found $($errors.Count) error(s) in $count script(s)."
    }
    Write-Output "PowerShell parser: PASS ($count script(s))"
}

Push-Location $repoRoot
try {
    Test-PowerShellScripts

    if (-not $SkipDotnet) {
        Invoke-NativeStep 'dotnet' @('test', '.\Memento.slnx', '--configuration', $Configuration, '--no-restore')
        Invoke-NativeStep 'dotnet' @('build', '.\Memento.slnx', '--configuration', $Configuration, '--no-restore')
    }

    if (-not $SkipLanguageValidation) {
        $reportPath = Join-Path $repoRoot 'artifacts\language-validation\synthetic-report.json'
        Invoke-NativeStep 'dotnet' @('run', '--project', '.\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj', '--configuration', $Configuration, '--no-restore', '--', '--output', $reportPath)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.Corpus -ne 'm04-synthetic-v1' -or $report.TotalCases -le 0 -or $report.CorrectionRequiredCount -ne 0) {
            throw "Synthetic language validation report failed its deterministic gate."
        }
        Write-Output ("Synthetic language validation: PASS ({0} case(s), 0 correction-required)" -f $report.TotalCases)
    }

    if ($VerifyMsix) {
        $msixPath = if ([string]::IsNullOrWhiteSpace($MsixPackagePath)) {
            Join-Path $repoRoot 'artifacts\msix\MEMENTO-0.1.0.0-unsigned.msix'
        }
        else {
            [System.IO.Path]::GetFullPath($MsixPackagePath)
        }
        if (-not (Test-Path -LiteralPath $msixPath -PathType Leaf)) {
            throw "MSIX package not found: $msixPath. Run scripts\Build-MementoMsix.ps1 first, or omit -VerifyMsix."
        }
        $msixArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '.\scripts\Test-MementoMsix.ps1', '-PackagePath', $msixPath)
        if ($RequireMsixSignature) { $msixArguments += '-RequireSignature' }
        Invoke-NativeStep 'powershell' $msixArguments
    }

    if (-not $SkipPreflight) {
        $preflightParameters = @{}
        if ($RequireCloudCredential) { $preflightParameters.RequireCloudCredential = $true }
        if ($RequireApplicationLock) { $preflightParameters.RequireApplicationLock = $true }
        if ($RequireAudioInput) { $preflightParameters.RequireAudioInput = $true }
        if ($RequireAudioOutput) { $preflightParameters.RequireAudioOutput = $true }
        if ($RequireArchiveIntegrity) { $preflightParameters.RequireArchiveIntegrity = $true }
        Invoke-NativeStep 'powershell' (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '.\scripts\Test-MementoPreflight.ps1') + $(foreach ($key in $preflightParameters.Keys) { "-$key" }))
    }

    Write-Output 'MEMENTO automation verification completed.'
}
finally {
    Pop-Location
}
