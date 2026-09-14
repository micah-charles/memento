# MEMENTO

MEMENTO is a proposed Windows-first, Cantonese-friendly AI companion whose most important output is a trustworthy, family-owned archive of a living person’s memories and conversational behaviour.

## Current status: M14 portable deployment implemented (live and supervised gates pending)

M00.1 architecture is approved for implementation. The repository now contains a self-contained WinUI 3 shell, local SQLite archive, crash-safe PCM capture, provider boundaries, clarification and provenance layers, export/backup, and health checks. Live provider credentials, physical microphone/native-window observation, OS-backed admin authentication, and real-user pilot review remain explicit gates; see [PROGRESS](docs/PROGRESS.md).

The governing principle is:

> Evidence first. Interpretation second. Simulation last.

## Review order

1. [Product vision](docs/PRODUCT_VISION.md)
2. [Architecture](docs/ARCHITECTURE.md)
3. [Memory and provenance model](docs/MEMORY_MODEL.md)
4. [Privacy model](docs/PRIVACY_MODEL.md)
5. [Technology decisions](docs/TECHNOLOGY_DECISIONS.md)
6. [OpenAI research](docs/OPENAI_RESEARCH.md)
7. [Security model](docs/SECURITY_MODEL.md)
8. [Cost model](docs/COST_MODEL.md)
9. [Roadmap and future acceptance tests](docs/ROADMAP.md)
10. [ADRs](docs/adr/)

Project state is kept in [PROGRESS](docs/PROGRESS.md), [DECISIONS](docs/DECISIONS.md), [KNOWN_ISSUES](docs/KNOWN_ISSUES.md), [EVIDENCE](docs/EVIDENCE.md), and the latest [implementation report](docs/OVERNIGHT_REPORT.md).

## Start the Windows app

From a Windows machine with the .NET 10 SDK and Windows App SDK build prerequisites installed:

```powershell
dotnet build .\src\Memento.App\Memento.App.csproj --configuration Release
Start-Process .\src\Memento.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Memento.App.exe
```

The app is currently an unpackaged self-contained executable. It stores local data under `%LOCALAPPDATA%\MEMENTO`; a signed MSIX package and enterprise installer registration remain deployment work.

Cloud transcription, replies, speech output, and launch-time retry processing are optional. To enable them, open **Windows Credential Manager → Windows Credentials → Add a generic credential**, set the target/address to `MEMENTO/OpenAI`, use any label such as `MEMENTO` for the user name, and put the API key in the password field. MEMENTO reads that credential at runtime and never writes it to the archive or logs. If the credential is absent, local recording and the local-only privacy mode still work.

To create a portable self-contained bundle:

```powershell
.\scripts\Publish-Memento.ps1
```

The publish step also writes `artifacts\MEMENTO-win-x64.zip.sha256`; `Install-Memento.ps1` verifies that sidecar automatically when it is present.

To install that bundle for the current Windows user, create a Start Menu shortcut, and register MEMENTO in Windows Installed apps:

```powershell
.\scripts\Install-Memento.ps1
```

To start MEMENTO after installation, or start the repository's published executable when no install exists:

```powershell
.\scripts\Start-Memento.ps1
```

Before a cloud-enabled pilot, verify the credential target without exposing its value:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential
```

If a family deployment requires the optional application lock, add `-RequireApplicationLock` to make its Credential Manager verifier a blocking preflight check.

To remove the installed app, shortcut, and per-user Installed apps entry while preserving the local archive by default:

```powershell
.\scripts\Uninstall-Memento.ps1
```

Pass `-RemoveData` only when the `%LOCALAPPDATA%\MEMENTO` archive has been backed up and should also be deleted. The in-app `啟用本機錄音功能` control independently enables or disables future recording; uninstalling does not silently delete the archive.

If the optional application-lock passcode is forgotten, close MEMENTO and run `.\scripts\Reset-MementoApplicationLock.ps1`. This clears only the current Windows user's `MEMENTO/AppLock` credential and leaves the archive data untouched; use `-WhatIf` to preview the action.

The optional in-app application lock can be configured from the participant shell. It stores only a salted verifier in the current Windows user's Credential Manager, blocks the shell and launch-time retry worker while locked, and is disabled by default. Choose a recovery policy before enabling it for a family deployment.

To generate the reproducible non-sensitive M04 synthetic validation report:

```powershell
dotnet run --project .\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj --configuration Release -- --output .\artifacts\language-validation\synthetic-report.json
```

## Non-goals for M00/M00.1

M00/M00.1 did not build a production application, start WinUI or SQLite runtime code, call an AI API, capture real audio, fine-tune a model, create a voice clone or avatar, require a local LLM/GPU, or create a cloud-hosted permanent family-memory database. Later milestones now implement local capture and archive foundations while live integrations remain gated.

## Repository shape

```text
.
├── docs/       Product, architecture, research, operating models, and ADRs
├── schemas/    Versioned documentation-level data contracts
├── src/        WinUI 3 shell and local archive implementation
├── tests/      Automated contract, persistence, privacy, and failure-path verification
├── scripts/    Publish, install, start, and uninstall helpers
├── tools/      Reproducible validation and maintenance utilities
└── samples/    Reserved for consented, synthetic, or redacted test fixtures
```

## Research currency

Current API, pricing, platform, privacy, and license claims in the documents are dated **2026-09-12** and include source URLs. Re-check those sources at the start of any implementation milestone; model aliases, pricing, endpoint behaviour, Windows SDK versions, and legal guidance can change.

## Git and public-repository hygiene

The intended public remote is `https://github.com/micah-charles/memento`. Real recordings, transcripts, exports, backups, logs, credentials, and API responses must never be committed. The initial `.gitignore` excludes those paths. No credentials are stored in this repository.
