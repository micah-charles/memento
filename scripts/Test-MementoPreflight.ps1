[CmdletBinding()]
param(
    [string]$BundlePath = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [long]$MinimumFreeBytes = 1073741824,
    [switch]$RequireCloudCredential,
    [switch]$RequireApplicationLock,
    [switch]$RequireAudioInput,
    [switch]$RequireAudioOutput,
    [switch]$RequireArchiveIntegrity
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($BundlePath)) { $BundlePath = Join-Path $repoRoot 'artifacts\MEMENTO-win-x64.zip' }
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path $localAppData 'MEMENTO\App' }
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $localAppData 'MEMENTO' }
$BundlePath = [System.IO.Path]::GetFullPath($BundlePath)
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)

$failures = 0
function Write-Check([string]$Name, [bool]$Passed, [string]$Detail, [string]$Severity = 'FAIL') {
    if ($Passed) {
        Write-Output ("PASS  {0}: {1}" -f $Name, $Detail)
        return
    }

    Write-Output ("{0}  {1}: {2}" -f $Severity, $Name, $Detail)
    if ($Severity -eq 'FAIL') { $script:failures++ }
}

$bundleExists = Test-Path -LiteralPath $BundlePath -PathType Leaf
Write-Check 'bundle' $bundleExists ($(if ($bundleExists) { $BundlePath } else { "not found at $BundlePath" }))
if ($bundleExists) {
    $sidecarPath = "$BundlePath.sha256"
    $sidecarExists = Test-Path -LiteralPath $sidecarPath -PathType Leaf
    Write-Check 'bundle checksum sidecar' $sidecarExists ($(if ($sidecarExists) { $sidecarPath } else { "not found at $sidecarPath" }))
    if ($sidecarExists) {
        $expected = (Get-Content -LiteralPath $sidecarPath -Raw).Trim().Split()[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $BundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Check 'bundle checksum' ($expected -match '^[0-9a-f]{64}$' -and $expected -eq $actual) "SHA-256 $actual"
    }
}

$installedExecutable = Join-Path $InstallRoot 'Memento.App.exe'
$installedExists = Test-Path -LiteralPath $installedExecutable -PathType Leaf
Write-Check 'installed executable' $installedExists ($(if ($installedExists) { $installedExecutable } else { "not found at $installedExecutable" }))

$installedUninstaller = Join-Path $InstallRoot 'Uninstall-Memento.ps1'
$uninstallerExists = Test-Path -LiteralPath $installedUninstaller -PathType Leaf
Write-Check 'app-local uninstaller' $uninstallerExists ($(if ($uninstallerExists) { $installedUninstaller } else { "not found at $installedUninstaller" }))

$installedRecoveryHelper = Join-Path $InstallRoot 'Reset-MementoApplicationLock.ps1'
$recoveryHelperExists = Test-Path -LiteralPath $installedRecoveryHelper -PathType Leaf
Write-Check 'app-lock recovery helper' $recoveryHelperExists ($(if ($recoveryHelperExists) { $installedRecoveryHelper } else { "not found at $installedRecoveryHelper" }))

$installedCredentialHelper = Join-Path $InstallRoot 'Set-MementoOpenAiCredential.ps1'
$credentialHelperExists = Test-Path -LiteralPath $installedCredentialHelper -PathType Leaf
Write-Check 'OpenAI credential setup helper' $credentialHelperExists ($(if ($credentialHelperExists) { $installedCredentialHelper } else { "not found at $installedCredentialHelper" }))

$installedCredentialRemovalHelper = Join-Path $InstallRoot 'Remove-MementoOpenAiCredential.ps1'
$credentialRemovalHelperExists = Test-Path -LiteralPath $installedCredentialRemovalHelper -PathType Leaf
Write-Check 'OpenAI credential removal helper' $credentialRemovalHelperExists ($(if ($credentialRemovalHelperExists) { $installedCredentialRemovalHelper } else { "not found at $installedCredentialRemovalHelper" }))

$audioAssemblyRoot = Split-Path -Parent $installedExecutable
$naudioCore = Join-Path $audioAssemblyRoot 'NAudio.Core.dll'
$naudioWinMm = Join-Path $audioAssemblyRoot 'NAudio.WinMM.dll'
$naudioWasapi = Join-Path $audioAssemblyRoot 'NAudio.Wasapi.dll'

try {
    # Loading the input adapter and enumerating wave-in capabilities is
    # read-only; this check never opens the microphone or starts a recording.
    if (-not (Test-Path -LiteralPath $naudioCore -PathType Leaf) -or -not (Test-Path -LiteralPath $naudioWinMm -PathType Leaf)) {
        throw 'installed NAudio input adapter assemblies are missing'
    }
    [System.Reflection.Assembly]::LoadFrom($naudioCore) | Out-Null
    [System.Reflection.Assembly]::LoadFrom($naudioWinMm) | Out-Null
    $inputDeviceCount = [NAudio.Wave.WaveInEvent]::DeviceCount
    $inputSeverity = if ($RequireAudioInput) { 'FAIL' } else { 'WARN' }
    Write-Check 'audio input devices' ($inputDeviceCount -gt 0) ("{0} Windows wave-in device(s) detected; no microphone was opened" -f $inputDeviceCount) $inputSeverity
}
catch {
    $inputSeverity = if ($RequireAudioInput) { 'FAIL' } else { 'WARN' }
    Write-Check 'audio input devices' $false ('unable to enumerate Windows wave-in devices: ' + $_.Exception.GetType().Name) $inputSeverity
}

try {
    # WASAPI render enumeration is also read-only; this check never opens an
    # output stream or plays audio.
    if (-not (Test-Path -LiteralPath $naudioCore -PathType Leaf) -or -not (Test-Path -LiteralPath $naudioWasapi -PathType Leaf)) {
        throw 'installed NAudio WASAPI adapter assemblies are missing'
    }
    [System.Reflection.Assembly]::LoadFrom($naudioCore) | Out-Null
    [System.Reflection.Assembly]::LoadFrom($naudioWasapi) | Out-Null
    $outputEnumerator = [NAudio.CoreAudioApi.MMDeviceEnumerator]::new()
    try {
        $outputDevices = $outputEnumerator.EnumerateAudioEndPoints(
            [NAudio.CoreAudioApi.DataFlow]::Render,
            [NAudio.CoreAudioApi.DeviceState]::Active)
        $outputDeviceCount = $outputDevices.Count
    }
    finally {
        $outputEnumerator.Dispose()
    }
    $outputSeverity = if ($RequireAudioOutput) { 'FAIL' } else { 'WARN' }
    Write-Check 'audio output devices' ($outputDeviceCount -gt 0) ("{0} active Windows render device(s) detected; no audio was played" -f $outputDeviceCount) $outputSeverity
}
catch {
    $outputSeverity = if ($RequireAudioOutput) { 'FAIL' } else { 'WARN' }
    Write-Check 'audio output devices' $false ('unable to enumerate Windows render devices: ' + $_.Exception.GetType().Name) $outputSeverity
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MEMENTO\MEMENTO.lnk'
Write-Check 'Start Menu shortcut' (Test-Path -LiteralPath $shortcutPath -PathType Leaf) $shortcutPath 'WARN'

$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MEMENTO'
$registeredInstall = $false
if (Test-Path -LiteralPath $uninstallRegistryPath) {
    try {
        $registeredInstall = [string]::Equals((Get-ItemProperty -LiteralPath $uninstallRegistryPath -Name InstallLocation -ErrorAction Stop).InstallLocation, $InstallRoot, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $registeredInstall = $false
    }
}
Write-Check 'Installed apps registration' $registeredInstall 'MEMENTO per-user uninstall registration' 'WARN'

$dataRootExists = Test-Path -LiteralPath $DataRoot -PathType Container
Write-Check 'archive directory' $dataRootExists ($(if ($dataRootExists) { $DataRoot } else { "not initialized at $DataRoot" })) 'WARN'
$databasePath = Join-Path $DataRoot 'data\memory.db'
$databaseExists = Test-Path -LiteralPath $databasePath -PathType Leaf
Write-Check 'archive database' $databaseExists ($(if ($databaseExists) { $databasePath } else { 'created on first launch' })) 'WARN'
if ($databaseExists) {
    $previousPath = $env:PATH
    try {
        $nativeSqlite = Join-Path $InstallRoot 'e_sqlite3.dll'
        if (-not (Test-Path -LiteralPath $nativeSqlite -PathType Leaf)) { throw 'installed e_sqlite3.dll is missing' }
        $env:PATH = "$InstallRoot;$previousPath"
        if (-not ('MementoNativeSqliteCheck' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MementoNativeSqliteCheck
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExecCallback(IntPtr argument, int columnCount, IntPtr values, IntPtr names);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_exec(IntPtr database, string sql, ExecCallback callback, IntPtr argument, out IntPtr error);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);

    public static IntPtr OpenReadOnly(string filename)
    {
        const int ReadOnly = 0x00000001;
        IntPtr database;
        var result = sqlite3_open_v2(filename, out database, ReadOnly, IntPtr.Zero);
        if (result != 0)
        {
            var message = database == IntPtr.Zero ? "SQLite could not open the database." : Marshal.PtrToStringAnsi(sqlite3_errmsg(database));
            if (database != IntPtr.Zero) sqlite3_close(database);
            throw new InvalidOperationException(string.Format("SQLite open failed ({0}): {1}", result, message));
        }
        return database;
    }

    public static string Scalar(IntPtr database, string sql)
    {
        string value = string.Empty;
        ExecCallback callback = (argument, columnCount, values, names) =>
        {
            if (columnCount > 0 && values != IntPtr.Zero)
            {
                var pointer = Marshal.ReadIntPtr(values);
                value = pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer);
                if (value == null) value = string.Empty;
            }
            return 0;
        };
        IntPtr error;
        var result = sqlite3_exec(database, sql, callback, IntPtr.Zero, out error);
        if (result != 0)
        {
            var message = error == IntPtr.Zero ? Marshal.PtrToStringAnsi(sqlite3_errmsg(database)) : Marshal.PtrToStringAnsi(error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            throw new InvalidOperationException(string.Format("SQLite query failed ({0}): {1}", result, message));
        }
        return value;
    }

    public static void Close(IntPtr database)
    {
        if (database != IntPtr.Zero) sqlite3_close(database);
    }
}
'@
        }
        $databaseHandle = [MementoNativeSqliteCheck]::OpenReadOnly($databasePath)
        try {
            $integrity = [MementoNativeSqliteCheck]::Scalar($databaseHandle, 'PRAGMA integrity_check')
            $schemaVersion = [MementoNativeSqliteCheck]::Scalar($databaseHandle, 'SELECT COALESCE(MAX(version), 0) FROM schema_migrations')
        }
        finally { [MementoNativeSqliteCheck]::Close($databaseHandle) }
        $integritySeverity = if ($RequireArchiveIntegrity) { 'FAIL' } else { 'WARN' }
        Write-Check 'archive integrity' ($integrity -eq 'ok') ("SQLite integrity_check=$integrity; schema version $schemaVersion") $integritySeverity
    }
    catch {
        $integritySeverity = if ($RequireArchiveIntegrity) { 'FAIL' } else { 'WARN' }
        Write-Check 'archive integrity' $false ('unable to verify SQLite archive: ' + $_.Exception.GetType().Name) $integritySeverity
    }
    finally { $env:PATH = $previousPath }
}

try {
    # cmdkey lists only credential metadata; it never prints the credential
    # secret. The target is optional for local-only use and can be promoted to
    # a blocking check for a cloud-enabled pilot with -RequireCloudCredential.
    $credentialListing = (& cmdkey.exe /list:MEMENTO/OpenAI 2>$null | Out-String)
    $credentialConfigured = -not [string]::IsNullOrWhiteSpace($credentialListing) -and $credentialListing -notmatch '\*\s*NONE\s*\*'
    $credentialDetail = if ($credentialConfigured) { 'MEMENTO/OpenAI target is present' } else { 'MEMENTO/OpenAI target is absent; local-only mode remains available' }
    $credentialSeverity = if ($RequireCloudCredential) { 'FAIL' } else { 'WARN' }
    Write-Check 'OpenAI credential target' $credentialConfigured $credentialDetail $credentialSeverity
}
catch {
    $credentialSeverity = if ($RequireCloudCredential) { 'FAIL' } else { 'WARN' }
    Write-Check 'OpenAI credential target' $false ('unable to inspect Windows Credential Manager: ' + $_.Exception.GetType().Name) $credentialSeverity
}

try {
    # The application-lock verifier is stored as a generic credential, but its
    # secret is never read or printed by this deployment check.
    $lockListing = (& cmdkey.exe /list:MEMENTO/AppLock 2>$null | Out-String)
    $lockConfigured = -not [string]::IsNullOrWhiteSpace($lockListing) -and $lockListing -notmatch '\*\s*NONE\s*\*'
    $lockDetail = if ($lockConfigured) { 'MEMENTO/AppLock target is present' } else { 'MEMENTO/AppLock target is absent; application lock remains optional' }
    $lockSeverity = if ($RequireApplicationLock) { 'FAIL' } else { 'WARN' }
    Write-Check 'Application lock credential' $lockConfigured $lockDetail $lockSeverity
}
catch {
    $lockSeverity = if ($RequireApplicationLock) { 'FAIL' } else { 'WARN' }
    Write-Check 'Application lock credential' $false ('unable to inspect Windows Credential Manager: ' + $_.Exception.GetType().Name) $lockSeverity
}

try {
    $driveRoot = [System.IO.Path]::GetPathRoot($DataRoot)
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    $freeBytes = $drive.AvailableFreeSpace
    Write-Check 'free disk space' ($freeBytes -ge $MinimumFreeBytes) ("{0:N0} bytes available; minimum {1:N0}" -f $freeBytes, $MinimumFreeBytes)
}
catch {
    Write-Check 'free disk space' $false $_.Exception.GetType().Name
}

$running = Get-Process -Name 'Memento.App' -ErrorAction SilentlyContinue
Write-Check 'MEMENTO process state' ($null -eq $running) ($(if ($null -eq $running) { 'not running; safe for install/update' } else { 'running; close before install/update' })) 'WARN'

Write-Output 'INFO  native GUI, microphone hardware, live provider exchange, and participant UX still require supervised target-machine verification.'
if ($failures -gt 0) {
    Write-Output "Preflight failed: $failures blocking check(s)."
    exit 1
}

Write-Output 'Preflight passed: no blocking deployment checks failed.'
