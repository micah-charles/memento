# MEMENTO implementation report

**Report date:** 2026-09-14
**Starting reviewed checkpoint:** `82cbccc` (`main` on `origin`)
**Current local checkpoint:** `b3e4c6c` (`ci: add Windows quality workflow`).
**Environment:** Windows, .NET 10 SDK, `win-x64`, repository worktree

This report records what is implemented and verified in the local worktree. It does not turn simulated, automated, or process-only checks into native GUI, hardware, live-provider, or participant evidence.

## Verification run

- `dotnet test tests\\Memento.Core.Tests\\Memento.Core.Tests.csproj --configuration Release --no-restore` — **170 passed, 0 failed**.
- `dotnet build Memento.slnx --configuration Release --no-restore` — **0 warnings, 0 errors**.
- M04 CLI — synthetic corpus `m04-synthetic-v1`, **14/14 PASS**, `correctionRequiredCount: 0`.
- Published bundle — `artifacts/MEMENTO-win-x64.zip`, 106,521,580 bytes, SHA-256 `aabf81ec62a88eac60c0bcecb63482e63b79165b5b4c882ecda571cdba600031`.
- Refreshed installed app — the latest bundle was installed, `Start-Memento.ps1` launched `MEMENTO` with `Responding=True`, normal close exited cleanly, and required audio enumeration/archive-integrity preflight passed; native GUI and physical audio behaviour remain supervised.
- Deployment preflight — **PASS** for bundle, sidecar, installed executable, Start Menu shortcut, archive paths, free disk, stopped-process state, one wave-in device, and one active WASAPI render device; no microphone was opened and no audio was played.
- Deployment policy gates — `Setup-Memento.ps1 -SkipPublish -RequireAudioInput -RequireAudioOutput -RequireArchiveIntegrity` passed; the same switches can promote missing enumerated devices or a failed archive integrity check to blocking failures without claiming physical capture/playback.
- Credential setup plumbing — the installed hidden-input helper created a disposable `MEMENTO/OpenAI` Credential Manager target, then the target was removed; no live provider request was made.
- Credential revocation plumbing — the installed fixed-target removal helper removed a disposable `MEMENTO/OpenAI` target after MEMENTO was closed, and `cmdkey.exe /list:MEMENTO/OpenAI` confirmed `* NONE *`; archive data was unchanged.
- Deployment preflight — **PASS** for the current-user Installed apps registration and its stable per-user install path.
- Repeatable automation gate — `scripts/Verify-MementoAutomation.ps1 -RequireAudioInput -RequireAudioOutput -RequireArchiveIntegrity` passed PowerShell parsing for 12 scripts, Release test **170/170**, Release build with **0 warnings/0 errors**, synthetic M04 validation (**14 cases, 0 correction-required**), and read-only deployment preflight.
- Scoped export privacy boundary — `ArchiveExporter.ExportRedacted` now emits a selected reviewed-Claim JSONL package with redacted Evidence and explicit withheld-Source labels; tests confirm no raw SQLite snapshot, media, transcript, provider payload, or annotation body is returned, and the Family Admin shell exposes the selector.
- Dependency security check — `dotnet list Memento.slnx package --vulnerable --include-transitive` reported no vulnerable packages across all four solution projects.
- Graceful close smoke — after a five-second launch, `CloseMainWindow()` returned true and the process exited within ten seconds with exit code 0; the handler now waits for retry/Realtime cleanup before disposing runtime services.
- Archive shutdown durability — `SqliteArchive.Dispose()` now performs a guarded `wal_checkpoint(TRUNCATE)` when the database exists; an idempotent disposal regression confirms an uninitialized archive is not created accidentally.
- Archive preflight — `Verify-MementoAutomation.ps1 -RequireArchiveIntegrity -RequireAudioInput -RequireAudioOutput` passed with `SQLite integrity_check=ok` and schema version 17, using the installed native SQLite library without loading app .NET assemblies into PowerShell.
- Credential setup check — default preflight emitted a **WARN** because `MEMENTO/OpenAI` is absent (local-only mode remains available); `-RequireCloudCredential` correctly returned one blocking failure without exposing a secret.
- Application-lock setup check — default preflight emitted an **optional WARN** because `MEMENTO/AppLock` is absent; `-RequireApplicationLock` correctly turns that policy choice into one blocking failure without exposing the verifier.
- Deployment workflow — `scripts/Setup-Memento.ps1 -SkipPublish` now composes install and preflight, with optional cloud/app-lock policy gates and `-Launch` support; the launch branch was smoke-tested and stopped cleanly.
- Installed process smoke — launched via `scripts\\Start-Memento.ps1`; observed title `MEMENTO` and `Responding=True` after seven seconds, then stopped cleanly. This is process evidence only; no visual GUI claim is made.
- Realtime shell output path — the separate live-conversation consent/action now persists provider PCM output as a verified derived WAV and routes it through the existing replay control. No live provider or physical output-device call was made.
- Realtime device path — the capture flow now opens a bounded WebSocket session before microphone capture, forwards local PCM chunks, commits on stop, and records policy-gated success/failure metadata. The transport and archive boundaries, including post-provider Source deletion, are automated-tested; live credentials and physical microphone/output verification remain open.
- MSIX deployment path — corrected the manifest schema, removed duplicate publish payloads from staging, and generated an unsigned package with portable Windows SDK BuildTools `makeappx.exe`; `makeappx unpack` verified one executable occurrence and the manifest. The signing path now accepts an explicit timestamp service or an offline development signature. A temporary development certificate produced a timestamped package that `signtool verify /pa` could verify, but current-user package deployment still requires a machine-trusted publisher certificate.
- MSIX verification hardening — `Test-MementoMsix.ps1` now validates the package identity/version, expected MEMENTO application entry, visual asset references, exact asset names, and approved PNG dimensions before reporting the payload gate as passed; the latest unsigned package passed this stricter check.
- MSIX artwork replacement — `Build-MementoMsix.ps1` now copies checked-in deterministic MEMENTO artwork from `packaging/assets` instead of generating 1x1 placeholders. The latest unsigned package is 108,730,462 bytes with SHA-256 `8bfd5f07b067a811196938991c127f58909d739554418c10dba97587bfd5aae4`; all four artwork dimensions were inspected and `Test-MementoMsix.ps1` passed. Publisher trust and target deployment remain open.
- Final automation rerun after the MSIX dimension gate — PowerShell parser **12/12**, Release tests **170/170**, Release build **0 warnings / 0 errors**, synthetic language validation **14/14**, and deployment preflight with required audio enumeration and archive integrity all passed.
- The clean-checkout workflow command (`Verify-MementoAutomation.ps1 -SkipPreflight`) was rerun locally and passed parser **12/12**, Release tests **170/170**, Release build **0 warnings / 0 errors**, and synthetic language validation **14/14**; the committed GitHub Actions workflow leaves machine-specific deployment checks separate.
- `Verify-MementoAutomation.ps1 -VerifyMsix` now delegates to the MSIX verifier and accepts `-MsixPackagePath` for signed outputs; its unsigned inspection passed, while the negative `-RequireMsixSignature` run rejected the unsigned package as expected. Supplying either policy/path switch now enables the MSIX gate automatically.

## Milestone status

| Milestone | Local status | Evidence and remaining gate |
|---|---|---|
| M01 | **IMPLEMENTED / AUTOMATED TESTED; native gate pending** | WinUI shell, SQLite schema/migrations, foreign keys, sessions, turns, consent, and Source metadata are implemented. See [M01 evidence](evidence/M01.md). Native visual/manual verification remains target-machine work. |
| M02 | **IMPLEMENTED / SIMULATED; hardware gate pending** | Crash-safe PCM WAV capture, checksums, recovery markers, failure cleanup, and deterministic fault tests are implemented. See [M02 evidence](evidence/M02.md). Physical microphone, permission, disconnect, disk-full, and controlled process interruption remain unverified. |
| M03 | **IMPLEMENTED / AUTOMATED TESTED; live gate pending** | Provider-neutral transcription, Responses reply, optional TTS, consent/privacy boundaries, durable retries, and failure preservation are implemented. See [M03 evidence](evidence/M03.md). A credentialed bounded exchange and real output device remain unverified. |
| M04 | **IMPLEMENTED / SYNTHETIC VERIFIED; empirical gate pending** | Reproducible language-validation harness, complete non-sensitive corpus, correction flag, code-switch segment checks, and JSON report CLI are implemented. See [M04 evidence](evidence/M04.md). Real Cantonese/mixed-language provider measurements remain unverified. |
| M05 | **IMPLEMENTED / AUTOMATED TESTED; participant UX gate pending** | Append-only clarification revisions, speaker authority, uncertainty/refusal/two-possibility outcomes, vocabulary/entity links, and provenance checks are implemented. See [M05 evidence](evidence/M05.md). Natural turn-taking with a real Cantonese speaker remains unobserved. |
| M06–M09 | **IMPLEMENTED / AUTOMATED TESTED** | Durable sessions/turns, retry worker and stale leases, candidate extraction, provenance, entities, lexical search, and privacy race boundaries are implemented. See [M06–M09 evidence](evidence/M06-M09.md). Live extraction quality, fuzzy identity review, and target-machine restart/power-loss checks remain open. |
| M10–M13 | **IMPLEMENTED / AUTOMATED TESTED** | Allowlisted current information, authenticated Family Admin review, export/backup/restore, withdrawal/deletion, health checks, and security/privacy regressions are implemented. See [M10–M13 evidence](evidence/M10-M13.md). Live search, admin UX/auth policy, target-machine restore, and destructive reliability drills remain open. |
| M14 | **PREPARATION / AUTOMATED VERIFIED** | Portable self-contained bundle, per-user install/update/uninstall, Start Menu shortcut, preflight, application lock, recording enable/disable setting, and pilot runbook are present. See [M14 evidence](evidence/M14.md). Signed MSIX, supervised pilot, and all target-machine gates remain open. |

## Architecture and security review

- Source → transcript revision → Evidence → Memory Claim separation remains intact; current-information results have no archive-write path.
- Speaker confirmation is distinct from Family Admin support; invalid authority transitions are rejected by tests.
- Private and local-only modes block cloud work at service boundaries and do not queue impossible extraction work.
- Provider adapters use `store=false`, preserve local audio before cloud work, redact operational error bodies, and treat transcript text as untrusted data.
- Backup restore stages extraction beside the target, rejects traversal/duplicates/normalized aliases, and leaves no partial target tree on unsafe input.
- Capture start now persists cloud consent from the participant checkbox and privacy mode; an unchecked cloud control can never be recorded as granted.
- Processing now locks privacy, recording, and cloud-consent controls until the bounded cloud turn finishes, preventing an in-flight policy race.
- Active capture markers now update their WAV data length after each append, and recovery scanning can recover complete PCM frames left behind by a stale header after interruption.
- Archive writes now require an exact Source-to-Turn match when a Source is already turn-linked; transcript revisions and queued jobs cannot silently drop that provenance by supplying a null TurnId.
- Entity resolution now offers conservative review-only name/alias similarity suggestions; short names require exact matches and suggestions never create identity or Evidence links automatically.
- Archive export now snapshots SQLite first and derives JSONL/media metadata from that same snapshot, preventing concurrent retry writes from producing a mixed-time bundle.
- Media-inclusive archive export now requires snapshot-listed files to exist and match recorded byte length and valid SHA-256 metadata; changed or missing media aborts the run and removes the incomplete export directory.
- The per-user installer now copies an app-local uninstall script and registers MEMENTO in Windows Installed apps; uninstall removes the app registration immediately and schedules app-tree cleanup after the script exits while preserving archive data by default.
- A forgotten application-lock passcode now has an explicit `Reset-MementoApplicationLock.ps1` recovery path that requires the app to be closed and preserves the archive; it supports `-WhatIf` for a safe preview.
- A real uninstall/reinstall exercise removed the app tree and registration, preserved the archive database hash, and restored the installed app from the matching bundle sidecar.
- WAV playback now marks cancellation before stopping the Windows output device, avoiding a stop-event race that could report an interrupted playback as successful.
- No credentials, recordings, transcripts, exports, backups, or provider responses were committed.

## Owner-gated next action

Run the supervised target-machine checklist in [PILOT_RUNBOOK.md](PILOT_RUNBOOK.md): start with native GUI and physical microphone verification, then use a disposable provider credential and redacted corpus, verify Family Admin and encrypted restore, and record incidents before any real participant pilot. Keep the milestone statuses above until those observations exist.
