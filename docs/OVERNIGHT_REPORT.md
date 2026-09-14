# MEMENTO implementation report

**Report date:** 2026-09-14  
**Starting reviewed checkpoint:** `82cbccc` (`main` on `origin`)  
**Final local checkpoint:** `dbecb75`  
**Environment:** Windows, .NET 10 SDK, `win-x64`, repository `C:\AI\memento`

This report records what is implemented and verified in the local worktree. It does not turn simulated, automated, or process-only checks into native GUI, hardware, live-provider, or participant evidence.

## Verification run

- `dotnet test tests\\Memento.Core.Tests\\Memento.Core.Tests.csproj --configuration Release --no-restore` — **120 passed, 0 failed**.
- `dotnet build Memento.slnx --configuration Release --no-restore` — **0 warnings, 0 errors**.
- M04 CLI — synthetic corpus `m04-synthetic-v1`, **14/14 PASS**, `correctionRequiredCount: 0`.
- Published bundle — `artifacts/MEMENTO-win-x64.zip`, 109,576,126 bytes, SHA-256 `1b94364b2568b48d5001a92eee3a76ec725596cdbd22dfe48791eca555d8b0f8`.
- Deployment preflight — **PASS** for bundle, sidecar, installed executable, Start Menu shortcut, archive paths, free disk, and stopped-process state.
- Installed process smoke — launched via `scripts\\Start-Memento.ps1`; observed title `MEMENTO` and `Responding=True` after seven seconds, then stopped cleanly. This is process evidence only; no visual GUI claim is made.

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
- No credentials, recordings, transcripts, exports, backups, or provider responses were committed.

## Owner-gated next action

Run the supervised target-machine checklist in [PILOT_RUNBOOK.md](PILOT_RUNBOOK.md): start with native GUI and physical microphone verification, then use a disposable provider credential and redacted corpus, verify Family Admin and encrypted restore, and record incidents before any real participant pilot. Keep the milestone statuses above until those observations exist.
