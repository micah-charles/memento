# `scripts/`

Scripts must not read or upload personal data by default.

`Publish-Memento.ps1` creates a self-contained `win-x64` publish directory and a zip bundle under `artifacts/`. It does not sign or install an MSIX package; a production installer still requires an owner-selected publisher identity and certificate.

`Install-Memento.ps1` installs that bundle under `%LOCALAPPDATA%\MEMENTO\App` and creates a per-user Start Menu shortcut without administrator access:

```powershell
.\scripts\Install-Memento.ps1
```

`Start-Memento.ps1` starts the installed app when available, or falls back to the repository's published executable:

```powershell
.\scripts\Start-Memento.ps1
```

`Uninstall-Memento.ps1` removes the app files and shortcut while preserving the `%LOCALAPPDATA%\MEMENTO` archive by default. Pass `-RemoveData` only after making and checking a backup:

```powershell
.\scripts\Uninstall-Memento.ps1
```

These helpers do not provide MSIX package identity, signing, or enterprise uninstall registration. Cloud features additionally require a Windows Credential Manager generic credential named `MEMENTO/OpenAI`.

Inside the app, `啟用本機錄音功能` is a persistent recording toggle. The shell also provides explicit health-check, media export, password-encrypted backup, disposable restore/manifest-verification, and Family Admin withdrawal actions; backups are written under `%LOCALAPPDATA%\MEMENTO\backups` and should be tested before pilot use. Withdrawing a Source preserves its local history and media but excludes it from future cloud processing, local search, and ordinary exports.
