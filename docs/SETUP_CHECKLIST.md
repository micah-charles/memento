# MEMENTO setup checklist

This checklist covers the verified local installation path. It does not turn
the supervised microphone, live provider, native visual, or participant gates
into completed evidence.

## Minimum local setup

Requirements:

- Windows x64 with the repository's .NET 10 and Windows App SDK prerequisites.
- The repository checkout, or an already-built `artifacts\MEMENTO-win-x64.zip` bundle.
- A local Windows user account that can write to `%LOCALAPPDATA%`.

From the repository root, run:

```powershell
.\scripts\Setup-Memento.ps1 -Launch
```

This publishes the self-contained bundle, verifies its SHA-256 sidecar, installs
MEMENTO under `%LOCALAPPDATA%\MEMENTO\App`, creates a Start Menu shortcut,
registers a per-user Installed apps entry, runs the read-only preflight, and
launches the app. The archive is stored separately under
`%LOCALAPPDATA%\MEMENTO`.

For an existing verified bundle, add `-SkipPublish`. For the strict local gate,
run:

```powershell
.\scripts\Verify-MementoAutomation.ps1 -VerifyLaunch `
  -RequireAudioInput -RequireAudioOutput `
  -RequireArchiveIntegrity -RequireInstalledPayloadMatch
```

The launch portion checks process title, responsiveness, and graceful close;
it is process evidence and does not replace native visual inspection.

## First launch

Inside MEMENTO:

1. Leave **啟用本機錄音功能** enabled if recording is wanted.
2. Select the privacy mode for the next recording.
3. Tick **我同意本機錄音** before pressing **開始錄音**.
4. Keep cloud consent unchecked for local-only use. `PRIVATE_CONVERSATION` and
   `LOCAL_CAPTURE_ONLY` prevent cloud work at the service boundary.
5. Use **檢查本機資料** after a test capture to inspect archive health.

The recording toggle is persistent and independent from per-session consent.
Turning it off prevents future captures; it does not delete existing Sources or
archive data.

## Optional cloud setup

Cloud transcription, responses, speech output, current-information search, and
launch-time retry work require an API credential for the current Windows user.
Enter it through the hidden-input helper rather than a command line:

```powershell
.\scripts\Set-MementoOpenAiCredential.ps1
.\scripts\Test-MementoPreflight.ps1 -RequireCloudCredential
```

The helper writes the `MEMENTO/OpenAI` Credential Manager target. No live
provider exchange has been verified in this repository. To disable future
cloud access, close MEMENTO and run:

```powershell
.\scripts\Remove-MementoOpenAiCredential.ps1
```

## Optional application lock

The lock is disabled by default. Configure it inside the app only after the
family has chosen a recovery policy. The verifier is stored in the current
Windows user's Credential Manager, not in the archive. To make the lock a
deployment requirement:

```powershell
.\scripts\Test-MementoPreflight.ps1 -RequireApplicationLock
```

If the passcode is forgotten, close MEMENTO and use
`Reset-MementoApplicationLock.ps1`; it clears only the fixed lock credential
and preserves the archive.

## Remove or update

To remove the app, shortcut, and per-user Installed apps entry while preserving
the archive:

```powershell
.\scripts\Uninstall-Memento.ps1
```

Use `-RemoveData` only after a checked backup and an explicit decision to delete
the local archive. Re-running `Install-Memento.ps1` with a verified bundle
stages a clean app tree and keeps the archive outside the replacement path.

## Pilot-only requirements

Before a real participant session, complete the supervised runbook in
[`PILOT_RUNBOOK.md`](PILOT_RUNBOOK.md): native GUI review, consented physical
microphone capture and interruption checks, disposable provider credential,
redacted language corpus, Family Admin policy and UX, encrypted restore, and
incident/rollback review.
