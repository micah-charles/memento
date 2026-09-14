[CmdletBinding()]
param(
    [string]$ExecutablePath = '',
    [ValidateRange(1, 120)]
    [int]$StartupTimeoutSeconds = 20,
    [ValidateRange(1, 120)]
    [int]$ShutdownTimeoutSeconds = 15
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

$existing = @(Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw 'MEMENTO is already running. Close it before running the launch smoke test.'
}

$workingDirectory = Split-Path -Parent $ExecutablePath
$process = $null
$closed = $false
try {
    $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory $workingDirectory -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $title = ''
    $responding = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "MEMENTO exited during launch with code $($process.ExitCode)."
        }

        try {
            $process.Refresh()
            $title = $process.MainWindowTitle
            $responding = $process.Responding
        }
        catch {
            # The process can transition between startup states while its
            # native window is being created. Re-poll until the deadline.
            $title = ''
            $responding = $false
        }

        if ($title -eq 'MEMENTO' -and $responding) {
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if ($process.HasExited) {
        throw "MEMENTO exited during launch with code $($process.ExitCode)."
    }
    if ($title -ne 'MEMENTO') {
        throw "MEMENTO did not expose the expected window title within $StartupTimeoutSeconds second(s); observed '$title'."
    }
    if (-not $responding) {
        throw "MEMENTO exposed the expected window title but was not responding within $StartupTimeoutSeconds second(s)."
    }
    Write-Output "PASS launch process evidence: title='$title'; responding=$responding; pid=$($process.Id); executable=$ExecutablePath"

    if (-not $process.CloseMainWindow()) {
        throw 'MEMENTO did not accept a graceful close request.'
    }
    if (-not $process.WaitForExit($ShutdownTimeoutSeconds * 1000)) {
        throw "MEMENTO did not exit within $ShutdownTimeoutSeconds second(s) after a graceful close request."
    }
    $closed = $true
    Write-Output "PASS graceful close process evidence: exit_code=$($process.ExitCode)"
}
finally {
    if ($null -ne $process -and -not $process.HasExited -and -not $closed) {
        try {
            [void]$process.CloseMainWindow()
            [void]$process.WaitForExit(2000)
        }
        catch {
            Write-Warning "Launch smoke cleanup could not close MEMENTO: $($_.Exception.Message)"
        }
    }
    if ($null -ne $process) {
        $process.Dispose()
    }
}
