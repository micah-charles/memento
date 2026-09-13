# MEMENTO evidence register

**Research/access date:** 2026-09-12  
**Scope:** M00 and M00.1 documentation/schema architecture only

## Repository evidence

| Check | Result |
|---|---|
| Working directory | `/Volumes/ExtremePro/AIWorkspace/MEMENTO` |
| Initial state | Empty directory; no pre-existing application or user files found. |
| Deliverables | Required README, docs, ADRs, schema, and reserved-directory READMEs created. |
| Production runtime | Not created. |
| Personal data | No real audio, transcript, export, backup, or credential created. |
| Network/API calls | No OpenAI API call made; research used public documentation pages only. |
| Git repository | Initialized on `main`; M00 checkpoint commit `b7049a5` (`docs: establish M00 research and architecture foundation`). |
| Git remote | `origin` configured as `https://github.com/micah-charles/memento` for fetch/push. At the initial M00 checkpoint, read-only `git ls-remote --heads origin` returned no refs, consistent with an empty/unpublished repository. |
| Git push | The M00 baseline was pushed to the configured public remote before this M00.1 correction; this correction has not been pushed by this task. |
| Working tree after checkpoint | Clean after the final verification commit; no generated or personal-data files present. |

## Source register

Current or time-sensitive claims should be re-checked before implementation. The main documents repeat the source URL next to the relevant decision; this register provides the minimum source set used for M00.

### OpenAI

| Topic | Source | Used for |
|---|---|---|
| Model catalogue | <https://developers.openai.com/api/docs/models> | Current model families, multilingual framing, realtime/audio categories. |
| Realtime API | <https://platform.openai.com/docs/api-reference/realtime?lang=javascript> | WebRTC/WebSocket/SIP and audio session boundary. |
| Realtime 2.1 Mini | <https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini> | Candidate live model capabilities and audio pricing. |
| GPT-Transcribe | <https://developers.openai.com/api/docs/models/gpt-transcribe> | Durable transcription, code-switching hints, per-minute price. |
| Responses create | <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> | JSON output, tools, web search, `store`, source inclusion. |
| GPT-5.6 Terra | <https://developers.openai.com/api/docs/models/gpt-5.6-terra> | Candidate text reasoning/extraction pricing and structured outputs. |
| GPT-4o Mini TTS | <https://developers.openai.com/api/docs/models/gpt-4o-mini-tts> | Optional turn-based speech output and audio pricing. |
| Data controls | <https://developers.openai.com/api/docs/guides/your-data> | Training, abuse monitoring, application state, retention controls. |

### Windows and storage

| Topic | Source | Used for |
|---|---|---|
| Windows app platform | <https://learn.microsoft.com/en-us/windows/apps/> | WinUI 3/Windows App SDK recommendation and support baseline. |
| WinUI 3 | <https://learn.microsoft.com/en-us/windows/apps/winui/winui3/> | Native UI, C#/.NET/XAML, Windows desktop positioning. |
| Windows Core Audio | <https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components> | WASAPI/Core Audio capture boundary and shared/exclusive considerations. |
| Credential handling | <https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords> | Credential Manager, DPAPI, secret minimisation and logging rules. |
| SQLite transactions | <https://www.sqlite.org/atomiccommit.html> | Atomic commit and crash/power-loss reasoning. |
| SQLite WAL | <https://www.sqlite.org/wal.html> | Reader/writer concurrency, WAL file handling, checkpointing. |
| SQLite foreign keys | <https://www.sqlite.org/foreignkeys.html> | Runtime enabling requirement. |
| SQLite backup | <https://www.sqlite.org/backup.html> | Consistent online backup snapshot. |
| SQLite FTS5 | <https://www.sqlite.org/fts5.html> | First local lexical-search index. |

### Audio preservation and privacy

| Topic | Source | Used for |
|---|---|---|
| Library of Congress sound preservation | <https://www.loc.gov/programs/national-recording-preservation-plan/sound-preservation/> | Broadcast WAV/96 kHz/24-bit preservation reference. |
| Library of Congress sound preferences | <https://www.loc.gov/preservation/digital/formats/content/sound_preferences.shtml> | PCM/BWF, higher sample rate/bit depth, metadata considerations. |
| IETF FLAC RFC | <https://www.rfc-editor.org/rfc/rfc9639.html> | Open, lossless, low-complexity derived audio option. |
| ICO consent | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/consent/how-should-we-obtain-record-and-manage-consent/> | Consent audit-trail fields and withdrawal. |
| ICO audio transparency | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/cctv-and-video-surveillance/guidance-on-video-surveillance-including-cctv/how-can-we-comply-with-the-data-protection-principles-when-using-surveillance-systems/> | Audio intrusiveness and clear recording signal. |
| ICO domestic purposes | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/exemptions/a-guide-to-the-data-protection-exemptions/> | Legal-scope caveat; not treated as blanket permission. |

### Reusable projects and licenses

| Project | Source | License/status read 2026-09-12 |
|---|---|---|
| OpenAI Whisper | <https://github.com/openai/whisper> | MIT shown on repository. |
| whisper.cpp | <https://github.com/ggml-org/whisper.cpp> | MIT shown on repository. |
| SQLite source | <https://github.com/sqlite/sqlite/blob/master/LICENSE.md> | Core source public domain; some build scripts have separate terms. |
| Windows App SDK | <https://github.com/microsoft/WindowsAppSDK> | MIT shown on repository. |
| Mem0 | <https://github.com/mem0ai/mem0> | Apache-2.0 shown on repository. |
| Letta | <https://github.com/letta-ai/letta> | Apache-2.0 shown on repository. |
| FLAC | <https://github.com/xiph/flac> | Libraries BSD-like; tools/plugins have separate GPL/GFDL terms. |

## Evidence interpretation

Research sources establish capabilities and constraints; they do not prove MEMENTO’s target-language accuracy, safety, cost, or user fit. Those claims require later test evidence, recorded in milestone-specific reports and never inferred from documentation alone.

## M00.1 verification evidence

- Five separate documentation-level JSON Schema contracts were added for Source, Evidence, Memory Claim, Response Episode, and Annotation; `memory-record.schema.json` remains a union entry point for portability.
- The architecture now requires explicit Source → Evidence → Memory Claim links, independent speaker/family/admin authority, temporal validity, and evidence-backed Response Episodes.
- The roadmap retains M01–M14 numbering and explicitly keeps M01 local-only; no WinUI code, SQLite runtime, microphone capture, OpenAI call, or application dependency was added by M00.1.
- The acceptance package includes repeated Evidence → one Claim, temporal change, family support ≠ speaker confirmation, and observed Response Episode scenarios.

## M01 implementation evidence — 2026-09-13

- A minimal WinUI 3 app and local SQLite core were added under `src/` with five automated tests under `tests/Memento.Core.Tests`.
- Every archive connection enables foreign keys, a 5-second busy timeout, WAL journaling, and `synchronous=FULL`; connection-level assertions cover these durability settings.
- `dotnet build Memento.slnx --configuration Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet test Memento.slnx --configuration Release --no-restore` passed 5/5 tests.
- `dotnet list tests/Memento.Core.Tests/Memento.Core.Tests.csproj package --vulnerable --include-transitive` reported no vulnerable packages after the native SQLite provider pin.
- The initial framework-dependent launch failed with a Windows “This application could not be started” window. After enabling self-contained Windows App SDK deployment, a direct launch produced a `MEMENTO` main window title and created the local database. Native-window visual/manual verification remains unavailable, so M01 is recorded as BLOCKED rather than PASS. Full details: [evidence/M01.md](evidence/M01.md).
- Implementation commit: `6419f48` (`feat: establish M01 local storage foundation`); this is not a passing M01 checkpoint.

## M02 implementation evidence — 2026-09-13

- `Memento.Core.Audio` contains a PCM WAV writer, validator, recovery scanner, capture controller, and provider-neutral audio-input contract. `WaveInAudioInput` adapts the Windows NAudio `WaveInEvent` path without exposing NAudio types to the archive/domain layer.
- The app UI requires an explicit `我同意本機錄音` consent checkbox before enabling `開始錄音`; while active it changes to `停止錄音` and displays a local-only recording state.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed 9/9 after the M02 additions.
- `dotnet build src/Memento.App/Memento.App.csproj --configuration Release` passed with 0 warnings and 0 errors.
- Automated tests cover deterministic fake-input capture, consent denial, finalized WAV structure, SHA-256, Source registration, recoverable `.capture.tmp` files, and corrupt partial preservation. They do not prove microphone hardware, Windows permission prompts, unplug/disconnect, disk exhaustion, or a real process crash.
- Capture lifecycle tests also cover synchronous input-start failure, input disposal failure, finalization failure, and Source-registration failure while preserving recovery state.
- Manual/native-window verification is unavailable because the Computer Use native surface currently reports `apps: []`; no visual PASS is claimed.
- Gate decision: **PARTIAL/BLOCKED** pending target-machine microphone and GUI verification. No cloud transmission is implemented in M02.

## M03 implementation evidence — 2026-09-13

- Official documentation was re-checked on 2026-09-13: [Realtime API reference](https://platform.openai.com/docs/api-reference/realtime?lang=javascript), [current model catalogue](https://developers.openai.com/api/docs/models), and [GPT-Transcribe model page](https://developers.openai.com/api/docs/models/gpt-transcribe). The Realtime reference documents WebRTC/WebSocket/SIP transport; the model catalogue identifies current speech models. These sources establish API shape only, not MEMENTO’s live success or Cantonese quality.
- Added migration 3 and the append-only `provider_interactions` table. The archive does not store provider-hosted conversation state as canonical memory.
- `ConversationOrchestrator` checks `LOCAL_CAPTURE_ONLY`, cloud consent, and local-file existence before transmission. It stores successful or failed interaction metadata and leaves the local file untouched on provider failure.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed **14/14** at the M03 checkpoint.
- No API key, OpenAI SDK, live request, or real family audio was added. Deterministic fake-provider tests prove the boundary and failure isolation only.
- Gate decision: **PARTIAL/BLOCKED** until a credentialed, bounded, consented live exchange can be run on target Windows hardware with local audio preserved first.

## M04 implementation evidence — 2026-09-13

- Added `LanguageValidationHarness` and synthetic case/observation/report contracts. Categories cover Hong Kong Cantonese, colloquial Cantonese, Mandarin, Cantonese/Mandarin and Cantonese/English code-switching, names, places, incomplete sentences, repetitions, hesitations, uncertainty, numbers, dates, and English product names.
- Metrics include transcript similarity, entity accuracy, code-switch preservation, uncertainty preservation, and latency. Case classifications deliberately keep weak entity recognition separate from ordinary wording variance.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed **18/18** after M04 additions.
- No real family data or real provider corpus was used. Gate decision: **PARTIAL/BLOCKED** pending empirical provider measurement.
- The validation result now records and aggregates whether participant correction is required; the name-mismatch and report tests cover this metric.

## M05 implementation evidence — 2026-09-13

- Added migrations 4–5 for `transcript_revisions`, `clarification_events`, and `vocabulary_entries`, each linked by foreign keys to the original Source/session and revision parent.
- The `ClarificationProtocol` preserves initial recognition, question, participant response, correction outcome, corrected revision, and speaker-confirmed vocabulary. Refusals and uncertain/two-option answers remain explicit without invented canonical values.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed **23/23** after M05 additions.
- Tests cover the `阿珍` → `阿貞` chain, uncertain school year, refusal, “唔記得”, correction of a correction, policy thresholds, and schema persistence. No participant or natural voice UX test was run.
- Gate decision: **PARTIAL/BLOCKED** pending participant-facing clarification UX and live Cantonese interaction review.

## M06–M09 implementation evidence — 2026-09-13

- Added migrations 6–8 for retryable conversation jobs, separate Evidence/Memory Claim/link records, and person/entity/alias links; migration 14 links extraction jobs to the exact transcript revision they must process; migration 15 records authenticated deletion tombstones; migration 16 adds a rebuildable FTS5 lexical index.
- The WinUI shell exposes the local FTS5/substring search service for transcript, Evidence, and candidate Claim text; withdrawn Sources remain excluded from displayed results.
- `ArchiveSearchService.Rebuild()` was verified after clearing the FTS table, recreating all canonical transcript/Evidence/Claim rows and restoring Cantonese search results.
- The shell exposes the same rebuild operation as `修復本機搜尋索引`; it reports the rebuilt row count in Cantonese status text.
- `ConversationSessionWriter` persists session/turn/source work and supports retry state transitions without deleting the local Source.
- `MemoryExtractionService` writes candidate Evidence and candidate Claims separately, with an explicit supports/weakens/contradicts/clarifies/contextualises link. `ProvenanceGraph.Validate` rejects mismatched source chains and unconfirmed AI inference promotion.
- `EntityResolutionService` adds people, speaker-confirmed aliases, and explicit Evidence links. Existing transcript and audio records remain unchanged.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed **27/27** after M06–M09 additions; `dotnet build Memento.slnx --configuration Release --no-restore` passed with 0 warnings and 0 errors.
- Gate status: **IMPLEMENTED / AUTOMATED TESTED**, with live worker scheduling, real provider extraction, and admin UI still unverified.

## M10–M13 implementation evidence — 2026-09-13

- Added the `ISearchProvider`/`CurrentInformationService` boundary and an `OpenAiWebSearchProvider` that sends an allowlisted Responses web-search request with `store=false`, filters citations to HTTPS hosts in the allowlist, and returns retrieval time, source URLs, and `IsUntrustedExternalInformation=true`; no service method writes personal memory.
- The WinUI shell now exposes an explicit current-information query using the same allowlist; it requires the cloud-consent control, refuses `LOCAL_CAPTURE_ONLY` sessions, and renders the returned source URL/snippet as untrusted external information without adding archive records.
- Revoking the cloud-consent control cancels an in-flight current-information request and suppresses any result that races with the revocation.
- Added `FamilyAdminReviewService` with an `IAdminAuthorizer` boundary, candidate listing, attributed review annotations, and explicit candidate status transitions. The test authorizer is fixed and clearly a fixture; it is not production identity management.
- Added `ArchiveExporter` JSONL/media/snapshot export with SHA-256 manifest entries and `ArchiveBackupProtector` AES-GCM password backup/restore.
- `ArchiveBackupProtector.ReencryptFile` was verified with a rotated password: the new password restores the same snapshot, while the old password is rejected.
- Export directories now use unique run IDs, and duplicate media basenames are preserved with collision-safe names instead of aborting an export.
- Added `ArchiveHealthCheck` for SQLite integrity, schema version, recoverable audio, due conversation jobs, media integrity, and canonical/search-index row parity.
- The WinUI shell now exposes explicit local health-check, media export, password-encrypted backup, disposable restore/manifest-verification, and Family Admin withdrawal actions; backup snapshots are staged under a temporary directory and removed after encryption, while restore never overwrites the active archive.
- Family Admin now has explicit confirmation flows for deleting the latest finalized Source and withdrawing it from future cloud processing. Deletion records a minimal tombstone and reports media-removal failures; withdrawal retains local history/media and writes an attributed annotation while filtering future processing, search, and default exports.
- Family Admin review now shows linked Evidence relationships, participant certainty, and speaker-confirmation state. Authorized Source playback validates the archived byte length, SHA-256, PCM WAV structure, and output errors before reporting completion; hardware speaker verification remains a live gate.
- Family Admin review now rejects attempts to create `speaker_confirmation` or `withdrawal` annotations through its review boundary; those authority-bearing actions remain limited to participant clarification or the authenticated withdrawal service.
- Export, encrypted backup, and restore-verification controls now require the same Family Admin authorization and write a minimal archive operation audit without recording paths or content.
- In-flight transcription, response, and speech-output calls re-check Source withdrawal and cloud consent before persisting success metadata, derived speech, or playback; deterministic tests cover these policy race boundaries.
- `dotnet test tests/Memento.Core.Tests/Memento.Core.Tests.csproj --configuration Release` passed **102/102**; full solution build passed with 0 warnings and 0 errors; the NuGet vulnerability scan reported no vulnerable packages.
- Gate status: **IMPLEMENTED / AUTOMATED TESTED**. Live search, real OS authentication, target-machine restore, and destructive reliability testing remain unverified.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Memento.ps1` completed successfully and produced the ignored local bundle `artifacts/MEMENTO-win-x64.zip` (106,457,937 bytes at the time of verification). The bundle is self-contained and portable; it is not a signed installer. Its SHA-256 sidecar was verified as `e4b254140d36049ea8a9ec5a660bdba5da1bb306175035712b485f36be3ae7f8`.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\Start-Memento.ps1` was smoke-tested against the published executable: MEMENTO remained running after 5 seconds and was then stopped cleanly. This verifies process launch only, not native-window visual content or microphone behaviour.
- The latest published bundle (106,457,937 bytes; SHA-256 `e4b254140d36049ea8a9ec5a660bdba5da1bb306175035712b485f36be3ae7f8`) was installed with `scripts\\Install-Memento.ps1`; its checksum preflight passed, the per-user executable and Start Menu shortcut were created, and the installed executable then launched successfully (`MEMENTO`, responding, 5-second smoke test). The installation remains in place for the next manual GUI/microphone gate.
- The installer update path was parser-checked and smoke-tested with a deliberately injected stale file in the installed app directory; the clean staging/swap removed that stale file while preserving the installed executable and archive data path.
- Installer failure handling now restores the previous app tree after any post-swap error, including Start Menu shortcut creation; the failed tree is removed only after the previous path is restored. A temporary injected post-swap failure was exercised against a disposable install root.
- The installer script was parser-checked and its checksum preflight was exercised against the published zip/sidecar; a mismatched sidecar now stops installation before extraction.
- After the capture-failure session fix, the regenerated bundle was launch-smoke-tested again: MEMENTO remained running after 5 seconds and stopped cleanly.
- Encrypted file and directory backups now write to a same-directory temporary file, flush it to disk, and replace the destination only after encryption completes; the replacement and cleanup path is covered by an automated test.
- Restore verification now requires `archive.sqlite`, validates SHA-256 entry shape and duplicates, reports malformed JSON, and flags files present in the restore that are absent from the manifest.
- `PcmWaveWriter.FinalizeAsset` now closes the writer and preserves the recoverable `.capture.tmp` when finalization or the final move fails; an automated duplicate-final-path test verifies the preservation path.
- `AudioCaptureController` now keeps synchronous input-start failures in `Failed`, cleans up the input/writer, and reports finalization or Source-registration failures through the same recovery event; deterministic tests cover start failure and archive registration failure.
- Source-registration failure after a successful WAV finalize now moves the file back to its `.capture.tmp` recovery marker; the regression test validates that the recovered marker is a valid PCM asset with its bytes intact.
- Input disposal failures during stop are also routed through the recovery path, preserving the temporary WAV for later scanning.
- Bounded cloud processing now rechecks the latest persisted cloud consent before transcription, response, and speech output, and rejects a Source that belongs to another session.
- OpenAI transcription, Responses, memory-extraction, and web-search adapters now reject malformed non-object/field shapes as bounded provider errors instead of leaking parser exceptions.
- The retry worker now reclaims `Processing` jobs whose five-minute lease expired, covering crash/restart recovery without retrying an actively running job.
- Archive health checks now count only stale `Processing` jobs as due, so active work is not reported as overdue while crashed work remains visible for recovery.
- Clarification outcomes now verify Source/session ownership and the persisted initial transcript revision before writing corrected revisions, preventing cross-session or orphaned provenance records.
- Clarification ownership is checked before corrected-revision insertion, and a sessionless-Source regression confirms no partial correction chain is written.
- Archive repository writes now enforce matching Source, Turn, Session, and transcript-revision context when those records already exist; regression coverage exercises mismatched and sessionless Source contexts.
- Async memory extraction now requires cloud consent before provider execution and re-checks consent and Source withdrawal after the provider returns, preventing post-revocation candidate persistence.
- Memory extraction persistence rejects the external-information Evidence kind, with deterministic coverage proving an `ExternalFact` candidate creates no Evidence, Claim, or link.
- Capture shutdown recovery now swallows input/writer cleanup failures, records the failure, and preserves the temporary audio marker without blocking session close.
- Family Admin review now re-reads the current candidate list before writing an annotation, so stale, reviewed, or withdrawn claims cannot receive a new review record.
- Source deletion now handles sessionless placeholder sources safely and reports why session-level assets cannot be associated with them.
- Source deletion now checks remaining Source and derived-audio references before removing a media path; a shared path is retained and recorded as a non-destructive finding. The regression suite covers this duplicate-reference case.
- The WinUI capture flow now persists a participant Turn before microphone start, links the finalized Source and provider metadata to that Turn, and closes it on normal stop or failure recovery. `ArchiveRepository.EndTurn` and `GetNextTurnSequence` are covered by a restart-safe lifecycle test.
- The WinUI stop control now remains enabled while consent controls are locked during capture, preventing a started recording from becoming un-stoppable through the participant UI.
- In-flight transcription and response operations re-check the current Source withdrawal state before persisting derived transcript/response metadata; deterministic tests prove withdrawn Sources cannot receive post-withdrawal revisions or successful provider records.
- OpenAI response and extraction adapters wrap transcripts in `<memento-transcript>` markers and state that the content is untrusted data, while retaining `store=false` and local schema/provenance gates.
