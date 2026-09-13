# `scripts/`

Scripts must not read or upload personal data by default.

`Publish-Memento.ps1` creates a self-contained `win-x64` publish directory and a zip bundle under `artifacts/`. It does not sign or install an MSIX package; a production installer still requires an owner-selected publisher identity and certificate.

`Install-Memento.ps1` installs that bundle under `%LOCALAPPDATA%\MEMENTO\App` and creates a per-user Start Menu shortcut without administrator access:

```powershell
.\scripts\Install-Memento.ps1
```

This helper does not provide MSIX package identity, signing, or enterprise uninstall registration.
