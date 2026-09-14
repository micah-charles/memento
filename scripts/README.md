# `scripts/`

Scripts must not read or upload personal data by default.

`Publish-Memento.ps1` creates a self-contained `win-x64` publish directory, a zip bundle, and a SHA-256 sidecar under `artifacts/`. It does not sign or install an MSIX package; a production installer still requires an owner-selected publisher identity and certificate.

`Setup-Memento.ps1` is the supported one-command deployment path. It publishes, installs, and runs the read-only preflight; use `-SkipPublish` when reusing an existing verified bundle, `-RequireCloudCredential`, `-RequireApplicationLock`, `-RequireAudioInput`, or `-RequireAudioOutput` for pilot policy gates, and `-Launch` to start the app after the checks:

```powershell
.\scripts\Setup-Memento.ps1 -RequireCloudCredential -Launch
```

`Verify-MementoAutomation.ps1` is the repeatable repository verification command. It parser-checks every PowerShell helper, runs the Release test and build without restoring packages, regenerates the deterministic M04 synthetic report, and runs the read-only deployment preflight. Pass `-VerifyMsix` after building a package to include manifest, payload, artwork-dimension, and signature-policy verification; it defaults to the unsigned artifact. Supplying `-MsixPackagePath .\artifacts\msix\MEMENTO-0.1.0.0.msix` or `-RequireMsixSignature` also enables this gate, so a signed output can be verified explicitly. Use `-SkipDotnet`, `-SkipLanguageValidation`, or `-SkipPreflight` when a narrower check is needed; the optional `-RequireCloudCredential`, `-RequireApplicationLock`, `-RequireAudioInput`, `-RequireAudioOutput`, and `-RequireArchiveIntegrity` switches promote those deployment policies to blocking checks:

```powershell
.\scripts\Verify-MementoAutomation.ps1 -RequireAudioInput -RequireAudioOutput -RequireArchiveIntegrity -VerifyMsix
```

`Install-Memento.ps1` installs that bundle under `%LOCALAPPDATA%\MEMENTO\App`, creates a per-user Start Menu shortcut, and registers MEMENTO in the current user's Windows Installed apps list without administrator access. The app-local `Uninstall-Memento.ps1`, `Reset-MementoApplicationLock.ps1`, `Set-MementoOpenAiCredential.ps1`, and `Remove-MementoOpenAiCredential.ps1` helpers are copied into the install tree so registration, forgotten-passcode recovery, credential setup, and credential removal remain usable after the repository is moved. Updates are staged as a clean tree and swapped into place, so files removed from a newer bundle cannot remain from an older install; any failure after the swap also moves the failed tree aside and restores the previous app. Install and uninstall refuse existing Windows reparse points in the target path, staging path, or optional data-removal path before moving or deleting directories. The archive data directory is outside the app tree and is preserved. When a matching `.zip.sha256` sidecar is present, it verifies the bundle before copying files:

Before extraction it also rejects rooted or traversal ZIP entries, duplicate entries after path normalization, and Unix symbolic-link entries. The bundle is therefore treated as untrusted input until its entry names and file types pass the install boundary.

```powershell
.\scripts\Install-Memento.ps1
```

`Start-Memento.ps1` starts the installed app when available, or falls back to the repository's published executable:

```powershell
.\scripts\Start-Memento.ps1
```

`Test-MementoLaunch.ps1` performs a repeatable process-level launch smoke. It
starts MEMENTO only when no existing instance is running, waits for the
`MEMENTO` window title and a responsive process, requests a graceful close, and
reports the exit code. This is process evidence only; it does not claim native
visual inspection, microphone capture, or Computer Use access:

```powershell
.\scripts\Test-MementoLaunch.ps1
```

`Test-MementoPreflight.ps1` performs a read-only deployment check for the bundle checksum, installed executable, shortcut, archive paths, free-space, running-process state, SQLite integrity/schema version through the bundled native SQLite library, and the Windows wave-in and active render devices exposed by the installed NAudio adapters. The audio probe only enumerates capabilities; it never opens the microphone, starts a recording, or plays audio. The credential, archive, and device checks are warnings by default; make them blocking for a pilot with `-RequireCloudCredential`, `-RequireApplicationLock`, `-RequireAudioInput`, `-RequireAudioOutput`, and/or `-RequireArchiveIntegrity`. Native GUI, physical microphone capture, live exchange, and participant checks remain supervised gates:

```powershell
.\scripts\Test-MementoPreflight.ps1
```

For a cloud-enabled pilot, require the credential target explicitly:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential
```

To configure the optional cloud credential without putting the API key in a command line, log, or source file, run the interactive helper. It prompts with hidden `SecureString` input and writes the credential directly to the current Windows user's Credential Manager:

```powershell
.\scripts\Set-MementoOpenAiCredential.ps1
```

Use `cmdkey.exe /delete:MEMENTO/OpenAI` after closing MEMENTO when the credential should be removed.

The installed removal helper performs the same fixed-target deletion with an explicit confirmation boundary:

```powershell
.\scripts\Remove-MementoOpenAiCredential.ps1
```

For a family deployment that requires the optional application lock, add `-RequireApplicationLock`; otherwise its Credential Manager target is reported as an optional warning:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential -RequireApplicationLock
```

For a cloud voice pilot that requires both enumerated devices before launch:

```powershell
.\scripts\Setup-Memento.ps1 -RequireCloudCredential -RequireApplicationLock -RequireAudioInput -RequireAudioOutput -RequireArchiveIntegrity -Launch
```

`Uninstall-Memento.ps1` removes the shortcut and current-user Installed apps registration immediately, then schedules deletion of the app files after the script exits; it preserves the `%LOCALAPPDATA%\MEMENTO` archive by default. Pass `-RemoveData` only after making and checking a backup:

```powershell
.\scripts\Uninstall-Memento.ps1
```

If the application-lock passcode is forgotten, close MEMENTO and use the explicit recovery helper below. It accepts only the current Windows user's fixed `MEMENTO/AppLock` target; the local archive is not touched. The command supports PowerShell `-WhatIf` before the deletion:

```powershell
.\scripts\Reset-MementoApplicationLock.ps1
```

`Build-MementoMsix.ps1` is the separate MSIX staging/signing path. It requires Windows SDK `makeappx.exe`, an owner-selected publisher identity, and (for an installable release) a matching certificate plus `signtool.exe`; when a certificate is supplied, its subject is checked against the manifest publisher before packaging. The tool resolver searches the full Windows SDK and the current user's `Microsoft.Windows.SDK.BuildTools` NuGet cache, or you can pass an explicit `-MakeAppxPath`/`-SignToolPath`. It is intentionally not part of the portable per-user flow. The staging step fails if more than one `Memento.App.exe` would enter the package. The optional `-TimestampUrl` parameter defaults to DigiCert's RFC 3161 service and can be set to an approved internal service or an empty string for an offline development signature. The Installed apps entry created by `Install-Memento.ps1` is a per-user registry registration and is intentionally not an MSIX package identity. Cloud features additionally require a Windows Credential Manager generic credential named `MEMENTO/OpenAI`.

`Test-MementoMsix.ps1` unpacks an MSIX with the same tool resolver and verifies the package identity/version, the expected MEMENTO application entry and visual asset references, exactly one root `Memento.App.exe`, no nested publish directory, the four expected package asset names, and their approved PNG dimensions. Pass `-RequireSignature` (or `-SignToolPath`) to add `signtool verify /pa`; unsigned package inspection reports a warning rather than pretending to prove installability:

```powershell
.\scripts\Test-MementoMsix.ps1
```

The M04 validation CLI can regenerate the non-sensitive synthetic report without provider credentials:

```powershell
dotnet run --project .\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj --configuration Release -- --output .\artifacts\language-validation\synthetic-report.json
```

Before installing a copied bundle, verify its sidecar with:

```powershell
Get-FileHash .\artifacts\MEMENTO-win-x64.zip -Algorithm SHA256
Get-Content .\artifacts\MEMENTO-win-x64.zip.sha256
```

Inside the app, `啟用本機錄音功能` is a persistent recording toggle. The shell also provides explicit health-check, interrupted-audio recovery, media export, password-encrypted backup, disposable restore/manifest-verification, and Family Admin withdrawal actions; backups are written under `%LOCALAPPDATA%\MEMENTO\backups` and should be tested before pilot use. Withdrawing a Source preserves its local history and media but excludes it from future cloud processing, local search, and ordinary exports.
The shell also offers an optional application lock backed by Windows Credential Manager. It is disabled by default; configure and test the recovery policy before using it for a family deployment.

The core library also exposes `ArchiveExporter.ExportRedacted(...)` for an explicitly selected set of reviewed Memory Claim IDs. It produces a portable JSONL package with Claim status, Evidence authority metadata, relationships, and attributed annotation types while withholding Source paths, transcript/audio content, provider payloads, and raw SQLite. The method rejects an empty, unknown, unreviewed, or withdrawn selection. The Family Admin shell exposes a multi-select reviewed-Claim picker beside the complete authenticated archive export path.
