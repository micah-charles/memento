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

The repository also has a Windows GitHub Actions quality workflow at `.github/workflows/windows-quality.yml`. It runs the PowerShell parser gate, Release tests/build, and deterministic M04 validation on a clean checkout; the local deployment preflight remains separate because it requires this machine's installed app, archive, and audio-device policy.

## Start the Windows app

From a Windows machine with the .NET 10 SDK and Windows App SDK build prerequisites installed:

```powershell
dotnet build .\src\Memento.App\Memento.App.csproj --configuration Release
Start-Process .\src\Memento.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Memento.App.exe
```

The app is currently an unpackaged self-contained executable. It stores local data under `%LOCALAPPDATA%\MEMENTO`; a signed MSIX package and enterprise installer registration remain deployment work.

Cloud transcription, replies, speech output, and launch-time retry processing are optional. To configure them without placing the API key in a command line, log, or source file, run the hidden-input helper:

```powershell
.\scripts\Set-MementoOpenAiCredential.ps1
```

The helper writes the `MEMENTO/OpenAI` generic credential for the current Windows user. MEMENTO reads that credential at runtime and never writes it to the archive or logs. If the credential is absent, local recording and the local-only privacy mode still work.

To disable future cloud access, close MEMENTO and run:

```powershell
.\scripts\Remove-MementoOpenAiCredential.ps1
```

To create a portable self-contained bundle:

```powershell
.\scripts\Publish-Memento.ps1
```

For the normal publish, install, and deployment-check flow in one command:

```powershell
.\scripts\Setup-Memento.ps1 -Launch
```

Add `-RequireCloudCredential`, `-RequireApplicationLock`, `-RequireAudioInput`, `-RequireAudioOutput`, `-RequireArchiveIntegrity`, and/or `-RequireInstalledPayloadMatch` when those pilot policies are mandatory. The archive and installed-payload checks are read-only; device checks only enumerate capabilities and do not replace physical capture/playback verification. Add `-SkipPublish` only when reusing an existing bundle whose sidecar has already been verified.

The publish step also writes `artifacts\MEMENTO-win-x64.zip.sha256`; `Install-Memento.ps1` verifies that sidecar automatically when it is present.

To install that bundle for the current Windows user, create a Start Menu shortcut, and register MEMENTO in Windows Installed apps:

```powershell
.\scripts\Install-Memento.ps1
```

To produce an MSIX package when a Windows SDK and an owner-selected publisher certificate are available, pass the certificate subject exactly as the manifest publisher:

```powershell
.\scripts\Build-MementoMsix.ps1 -Publisher 'CN=Your Publisher' -CertificatePath .\certs\memento.pfx
```

The script also supports an unsigned output for package inspection, but an unsigned MSIX is not an ordinary installable release. The checked-in manifest now uses deterministic MEMENTO artwork from `packaging/assets`; publisher identity, certificate trust, package validation, and update policy still need approval before distribution. The portable per-user installer above remains the supported path until those gates are approved.

To repeat the MSIX payload verification (manifest, single executable, assets, dimensions, and optional signature):

```powershell
.\scripts\Test-MementoMsix.ps1
```

Add `-VerifyMsix` to `Verify-MementoAutomation.ps1` when a package has been built so the complete repository gate also checks the MSIX artifact. Use `-MsixPackagePath .\artifacts\msix\MEMENTO-0.1.0.0.msix -RequireMsixSignature` to verify a signed output.

To start MEMENTO after installation, or start the repository's published executable when no install exists:

```powershell
.\scripts\Start-Memento.ps1
```

To run the repeatable process-level launch and graceful-close smoke (without
claiming native visual inspection):

```powershell
.\scripts\Test-MementoLaunch.ps1
```

The same smoke can be included in the repository gate with
`-VerifyLaunch` after the app is installed.

Before a cloud-enabled pilot, verify the credential target without exposing its value:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential
```

If a family deployment requires the optional application lock, add `-RequireApplicationLock` to make its Credential Manager verifier a blocking preflight check.

To remove the installed app, shortcut, and per-user Installed apps entry while preserving the local archive by default:

```powershell
.\scripts\Uninstall-Memento.ps1
```

Pass `-RemoveData` only when the `%LOCALAPPDATA%\MEMENTO` archive has been backed up and should also be deleted. The in-app `啟用本機錄音功能` control independently enables or disables future recording; uninstalling does not silently delete the archive. Family Admin can use `整理未完成錄音` in the shell to repair valid interrupted capture markers into reviewable `recovered` Sources.

If the optional application-lock passcode is forgotten, close MEMENTO and run `.\scripts\Reset-MementoApplicationLock.ps1`. This clears only the current Windows user's fixed `MEMENTO/AppLock` credential and leaves the archive data untouched; use `-WhatIf` to preview the action.

The optional in-app application lock can be configured from the participant shell. It stores only a salted verifier in the current Windows user's Credential Manager, blocks the shell and launch-time retry worker while locked, and is disabled by default. Choose a recovery policy before enabling it for a family deployment.

Family Admin can use **更新備份密碼** in the shell to verify an existing `.memento` bundle and create a new encrypted copy with a new password. The original backup is left untouched; this rotates a known password and does not replace a family-owned recovery policy.

To generate the reproducible non-sensitive M04 synthetic validation report:

```powershell
dotnet run --project .\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj --configuration Release -- --output .\artifacts\language-validation\synthetic-report.json
```

To evaluate a consented and redacted provider result set, keep the JSON outside
the repository and pass it as an input dataset. Each case contains `caseId`,
`category`, `expectedTranscript`, optional expected entities and code-switch
requirements, plus an `observation` with `observedTranscript`, observed
entities, `latencyMs`, and an optional `uncertaintyPreserved` flag:

```powershell
dotnet run --project .\tools\Memento.LanguageValidation\Memento.LanguageValidation.csproj --configuration Release -- --input C:\path\to\redacted-m04.json --output .\artifacts\language-validation\provider-report.json
```

The input reader rejects empty datasets, missing observations, duplicate case
IDs, and negative latency. Synthetic output is labelled `m04-synthetic-v1`;
external reports use the anonymous `m04-external-redacted-v1` label by default.
If a non-sensitive run identifier is needed, pass `--corpus` with only letters,
numbers, dots, dashes, or underscores. The input filename is never copied into
the report, and the report does not contain audio or credentials.

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

Current API, pricing, platform, privacy, and license claims in the documents are dated **2026-09-14** and include source URLs. Re-check those sources at the start of any implementation milestone; model aliases, pricing, endpoint behaviour, Windows SDK versions, and legal guidance can change.

## Git and public-repository hygiene

The intended public remote is `https://github.com/micah-charles/memento`. Real recordings, transcripts, exports, backups, logs, credentials, and API responses must never be committed. The initial `.gitignore` excludes those paths. No credentials are stored in this repository.
