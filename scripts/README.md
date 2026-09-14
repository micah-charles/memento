# `scripts/`

Scripts must not read or upload personal data by default.

`Publish-Memento.ps1` creates a self-contained `win-x64` publish directory, a zip bundle, and a SHA-256 sidecar under `artifacts/`. It does not sign or install an MSIX package; a production installer still requires an owner-selected publisher identity and certificate.

`Install-Memento.ps1` installs that bundle under `%LOCALAPPDATA%\MEMENTO\App`, creates a per-user Start Menu shortcut, and registers MEMENTO in the current user's Windows Installed apps list without administrator access. The app-local `Uninstall-Memento.ps1` is copied into the install tree so that registration remains usable after the repository is moved. Updates are staged as a clean tree and swapped into place, so files removed from a newer bundle cannot remain from an older install; any failure after the swap also moves the failed tree aside and restores the previous app. The archive data directory is outside the app tree and is preserved. When a matching `.zip.sha256` sidecar is present, it verifies the bundle before copying files:

```powershell
.\scripts\Install-Memento.ps1
```

`Start-Memento.ps1` starts the installed app when available, or falls back to the repository's published executable:

```powershell
.\scripts\Start-Memento.ps1
```

`Test-MementoPreflight.ps1` performs a read-only deployment check for the bundle checksum, installed executable, shortcut, archive paths, free disk space, and running-process state. It also checks whether the `MEMENTO/OpenAI` Windows Credential Manager target exists without reading its secret. The credential is a warning by default because local-only capture does not need it; make it blocking for a cloud-enabled pilot with `-RequireCloudCredential`. Native GUI, microphone, live exchange, and participant checks remain supervised gates:

```powershell
.\scripts\Test-MementoPreflight.ps1
```

For a cloud-enabled pilot, require the credential target explicitly:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential
```

`Uninstall-Memento.ps1` removes the shortcut and current-user Installed apps registration immediately, then schedules deletion of the app files after the script exits; it preserves the `%LOCALAPPDATA%\MEMENTO` archive by default. Pass `-RemoveData` only after making and checking a backup:

```powershell
.\scripts\Uninstall-Memento.ps1
```

If the application-lock passcode is forgotten, close MEMENTO and use the explicit recovery helper below. It removes only the current Windows user's `MEMENTO/AppLock` credential; the local archive is not touched. The command supports PowerShell `-WhatIf` before the deletion:

```powershell
.\scripts\Reset-MementoApplicationLock.ps1
```

These helpers do not provide MSIX package identity, signing, or enterprise installer registration. The Installed apps entry is a per-user registry registration and is intentionally not an MSIX package identity. Cloud features additionally require a Windows Credential Manager generic credential named `MEMENTO/OpenAI`.

The M04 validation CLI can regenerate the non-sensitive synthetic report without provider credentials:

```powershell
dotnet run --project .\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj --configuration Release -- --output .\artifacts\language-validation\synthetic-report.json
```

Before installing a copied bundle, verify its sidecar with:

```powershell
Get-FileHash .\artifacts\MEMENTO-win-x64.zip -Algorithm SHA256
Get-Content .\artifacts\MEMENTO-win-x64.zip.sha256
```

Inside the app, `啟用本機錄音功能` is a persistent recording toggle. The shell also provides explicit health-check, media export, password-encrypted backup, disposable restore/manifest-verification, and Family Admin withdrawal actions; backups are written under `%LOCALAPPDATA%\MEMENTO\backups` and should be tested before pilot use. Withdrawing a Source preserves its local history and media but excludes it from future cloud processing, local search, and ordinary exports.
The shell also offers an optional application lock backed by Windows Credential Manager. It is disabled by default; configure and test the recovery policy before using it for a family deployment.
