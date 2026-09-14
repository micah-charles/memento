# MEMENTO progress

**Current milestone:** M14 — Portable deployment and supervised pilot readiness
**Status:** PARTIAL / AUTOMATED TESTED; live provider, hardware, authenticated admin UX, target restore, and pilot gates remain pending
**Last updated:** 2026-09-14

## Completed in M00

- Confirmed the project scope from the master prompt: Windows-first, Cantonese-friendly, local family-owned archive, cloud-assisted conversation.
- Created the documentation-only repository structure and public-repository hygiene rules.
- Documented the product vision, architecture, provider boundaries, storage layout, privacy modes, evidence model, security threats, cost model, technology choices, roadmap, and ADRs.
- Refined the M00 contract to `1.1-draft` with separate Source, Evidence, Memory Claim, Response Episode, and Annotation schemas.
- Separated participant certainty, speaker confirmation, family assessment, and admin annotation; added temporal validity and event-time versus recorded-time semantics.
- Promoted Response Episode to a first-class observed domain concept and documented derived behavioural patterns as linked inference only.
- Researched current OpenAI realtime, transcription, Responses/tools, model, speech-output, privacy/retention, and pricing material using official sources.
- Researched Windows App SDK/WinUI, Core Audio/WASAPI, Credential Manager/DPAPI, SQLite transactions/WAL/FTS5/backup, archival audio, and reusable open-source projects using primary or authoritative sources.
- Encoded the original scenarios plus M00.1 acceptance scenarios for repeated Evidence → one Claim, temporal change, family support versus speaker confirmation, and Response Episode provenance.

## Verification performed

- Required documentation paths exist.
- Markdown links point to repository paths or explicit source URLs.
- All JSON Schema files parse as JSON, and the compatibility entry point references the separate domain contracts.
- No production code, API keys, recordings, transcripts, exports, or backups were created.
- Git remote setup and final status are recorded in [EVIDENCE.md](EVIDENCE.md).

## M00/M00.1 exit criteria

- [x] Product vision is explicit about evidence first and non-goals.
- [x] Architecture defines conversation, transcription, reasoning, search, memory extraction, storage, and export boundaries.
- [x] Original Sources, Evidence, uncertainty, corrections, contradictions, temporal validity, and full provenance are defined.
- [x] Cantonese/mixed-language validation is treated as a measured gate, not an unsupported promise.
- [x] Privacy modes, consent, credential handling, deletion, backup, export, and offline degradation are documented.
- [x] Cost estimates cover 15/30/60 minutes per day with dated assumptions.
- [x] M01–M05 acceptance criteria, three master-prompt scenarios, and M00.1 architecture scenarios are documented.
- [x] Technology recommendation and exact next implementation step are documented.
- [ ] Human architecture review and approval.

## M01 implementation attempt — 2026-09-13

- Implemented a minimal WinUI 3 shell and a local SQLite archive foundation in `src/Memento.App` and `src/Memento.Core`.
- SQLite connections now use WAL, a 5-second busy timeout, and `synchronous=FULL` to improve concurrent-write and power-loss behavior.
- Added five automated tests for migration, restart persistence, foreign keys, source metadata, relationships, and integrity.
- Solution build succeeded and vulnerability scan is clean after pinning the SQLite native provider to 2.1.13.
- M01 is **BLOCKED**, not passed: the current execution surface cannot visually inspect a native Windows window or prove clean-install/manual shell behaviour. See [M01 evidence](evidence/M01.md).
- M01 remains blocked for the visual/manual gate because native-window observation is unavailable in this execution surface.

## M02 implementation attempt — 2026-09-13

- Added crash-safe PCM WAV capture under a date-organized local `raw/audio` directory.
- Capture writes a `.capture.tmp` file, flushes each append, validates the WAV, computes SHA-256, atomically renames the file, and only then registers finalized `SourceMetadata`.
- Added a provider-neutral `IAudioInput` boundary and a Windows NAudio `WaveInEvent` adapter. The UI now requires an explicit local-recording checkbox and exposes large start/stop controls.
- The capture control remains enabled for stopping while the consent controls are locked during recording; Family Admin review is disabled while capture or processing is active.
- Capture failures now end the in-progress session in the WinUI shell while leaving the `.capture.tmp` recovery marker available for inspection.
- Capture identifiers are validated as single safe file-name components before any WAV path is created, preventing path traversal through malformed session/source IDs.
- If a new capture fails, the shell clears its active session/Source pairing so a previous recording cannot be accidentally processed with the failed session's identity.
- If WAV finalization succeeds but SQLite Source registration fails, the finalized file is moved back to its `.capture.tmp` marker so restart recovery can surface intact audio instead of leaving an untracked orphan file.
- Added an authenticated Family Admin recovery operation that repairs valid interrupted markers and registers them as `recovered` Sources; failed registration leaves a valid marker for retry.
- New capture markers retain generated Session and Turn IDs so recovery can restore Turn linkage; legacy markers continue to recover without inventing a Turn.
- The shell reloads the latest finalized local Source and Session after restart, so a consented normal session can be processed later without losing its local provenance.
- Added deterministic tests for consent gating, normal finalization, checksum and metadata registration, recoverable partial files, and corrupt partial preservation. These tests use a fake input; physical microphone, permission, disconnect, disk-full, and crash tests remain unverified.
- M02 is **PARTIAL/BLOCKED**, not passed: automated coverage is green, but physical microphone and native UI observation have not been verified in this environment. See [M02 evidence](evidence/M02.md).

## M03 implementation attempt — 2026-09-13

- Re-checked official OpenAI documentation on 2026-09-13. The current Realtime reference describes WebRTC/WebSocket/SIP audio sessions; the current model catalogue lists GPT-Transcribe for high-accuracy speech-to-text and GPT-Live-Transcribe for low-latency transcription. The archive therefore keeps transport, transcription, reasoning, and model identifiers behind provider-neutral contracts.
- Added `ConversationOrchestrator`, `IConversationProvider`, `ITranscriptionProvider`, and `IReasoningProvider` contracts. The orchestrator requires a finalized local audio path and explicit cloud consent before a provider call, and it refuses cloud processing in `LOCAL_CAPTURE_ONLY` mode.
- Added append-only `provider_interactions` metadata with provider, capability, model, snapshot, request ID, timestamps, usage, success, and redacted error fields. Provider failures are recorded without deleting or changing local audio.
- Added a deterministic provider for offline contract tests. No OpenAI credential or live network call is present in the repository.
- M03 is **PARTIAL/BLOCKED**: 14 automated tests pass at that checkpoint, but a live bounded voice interaction and target-machine microphone path remain unverified. See [M03 evidence](evidence/M03.md).
- A turn-based fallback is now available through optional OpenAI transcription and Responses adapters: finalized local WAV → transcription → text response, with `store=false` and provider metadata. Tests use HTTP handlers only; no live credentialed request was made.
- Added an optional `OpenAiRealtimeWebSocketProvider` with an injectable WebSocket transport, mono PCM16 chunking, explicit session configuration, response text/audio event parsing, output-size limits, cancellation-safe disposal, and a `RealtimeConversationOrchestrator` that requires the dedicated `LiveCloudConversation` consent scope. Deterministic protocol tests cover event parsing, audio collection, consent gating, and interaction metadata; no live Realtime account call was made.
- Optional speech output now records a `speech_output` provider interaction and `DerivedAudioStore` persists generated audio with an atomic rename, SHA-256 verification, and a separate `derived_speech_assets` table; derived audio is never registered as participant evidence.
- The WinUI shell now exposes a separate cloud-consent checkbox and a post-capture “轉錄及回覆最近錄音” action wired to the bounded provider pipeline; local-only sessions remain ineligible for cloud processing and failures retain the local Source.
- Retryable transcription and response provider/network failures now enqueue durable `durable_transcription` or `durable_response` jobs with backoff metadata while preserving the finalized Source; invalid local-file errors are not queued. Response retries reuse the persisted transcript and re-check cloud consent.

## M04 implementation attempt — 2026-09-13

- Added a reproducible language-validation harness with explicit Hong Kong Cantonese, colloquial Cantonese, Mandarin, mixed-language, names, places, dates, numbers, hesitations, repetitions, uncertainty, and product-name categories.
- The harness reports transcript similarity, entity/name accuracy, code-switch preservation, uncertainty preservation, latency, and a case-level disposition (`PASS`, `ACCEPTABLE_WITH_CLARIFICATION`, `WEAK`, `FAIL`, or `NOT_TESTED`). Name/entity accuracy is measured separately because a fluent sentence with a wrong person name is unsafe.
- Each result now exposes a machine-readable `CorrectionRequired` flag, and reports count cases that need participant clarification instead of inferring it from display text.
- Fixtures are synthetic and contain no family recordings or private transcripts.
- M04 is **PARTIAL/BLOCKED**: 18 deterministic tests pass cumulatively, but no real provider corpus or credentialed Cantonese measurement has been run. See [M04 evidence](evidence/M04.md).

## M05 implementation attempt — 2026-09-13

- Added append-only transcript revisions, clarification events, and speaker-confirmed vocabulary entries with foreign-key links to the original Source and session.
- Added a clarification policy that prioritises names, places, relationships, dates, identity, preferences, and other high-impact ambiguity while avoiding low-impact filler checks. Speaker correction text is stored verbatim and always outranks model confidence.
- The protocol supports confirmation, refusal, “唔記得”, two possibilities, and correction of a previous correction. A corrected revision never overwrites the initial recognition.
- Clarification persistence now commits the corrected revision, clarification event, and speaker-confirmed vocabulary entry atomically; a failed later event insert cannot leave an orphaned corrected revision.
- M05 is **PARTIAL/BLOCKED**: 23 deterministic tests pass cumulatively, but participant UX and natural Cantonese turn-taking have not been observed. See [M05 evidence](evidence/M05.md).

## M06–M09 implementation attempt — 2026-09-13

- M06 added durable `conversation_jobs` with pending/processing/succeeded/failed states, attempt counts, retry timestamps, and error metadata. `ConversationSessionWriter` maps the continuous session UX onto durable sessions, turns, local Sources, and retryable work; retries re-check cloud consent and policy-blocked jobs become terminal failures instead of looping. When a credential is available, the app starts the routed background worker at launch and keeps it cancellable with the window lifetime.
- Processing jobs now have a five-minute stale lease; a subsequent worker pass can reclaim work left in `Processing` by a crashed process while leaving fresh active work alone.
- Worker claims now use an atomic `status + updated_at` compare-and-set, so a second process skips a job already claimed by another worker instead of duplicating provider work.
- M07 added candidate `EvidenceRecord`, `MemoryClaim`, and explicit `EvidenceClaimLink` records. Evidence now carries optional audio spans and extraction provider/model metadata; `MemoryExtractionService` uses a deterministic provider boundary and never promotes candidates beyond `candidate` status. An async OpenAI structured-output adapter and persistence service now validate candidate fields before writing the same reviewable records, and consented app sessions queue revision-scoped `durable_extraction` work for the launch worker; corrected transcript revisions therefore receive their own extraction job; no live extraction call has been made.
- Extraction persistence drops `ExternalFact` candidates so untrusted current-information results cannot enter participant-memory claims through a malformed or prompt-injected provider response.
- Evidence, candidate claims, links, and their search-index rows are now persisted as one SQLite transaction; a late constraint or storage failure rolls back the complete extraction batch instead of leaving partial memory records.
- M08 added `ProvenanceGraph.Validate`, which checks Source → transcript revision → Evidence → Claim links, allowed relationships, blocks an unconfirmed AI inference from silently becoming a reviewed claim, and validates observed Response Episodes from stimulus/response Evidence.
- M09 added person entities, speaker-confirmed aliases, and Evidence-to-entity links. Alias history is additive and does not rewrite transcript or Source records.
- M09 now exposes conservative review-only display-name/alias similarity suggestions. Short names require exact matches, and suggestions never auto-link identity or Evidence; participant/Family Admin confirmation remains authoritative.
- Speaker-confirmed aliases and vocabulary now require a linked clarification event with a speaker-confirmed outcome; refusal, uncertainty, and missing-event inputs cannot create participant authority.
- M06–M09 now include a rebuildable SQLite FTS5 lexical index and `ArchiveSearchService` for transcript, Evidence, and candidate Claim text; its public `Rebuild()` operation recreates the index from canonical tables, while Cantonese substring fallback keeps short CJK queries usable and withdrawn/deleted Source records stay out of results. A real Chinese corpus is still needed to measure ranking/tokenization quality.
- Search index migration 17 now reconstructs transcript rows with the owning Source session, and incremental index writes derive that session when callers provide only a Source ID; search results therefore retain session provenance after upgrades as well as after a manual rebuild.
- The latest-source selector now includes `recovered` audio assets, so an interrupted recording promoted by Family Admin remains available for playback, review, export, and (when policy permits) later processing.
- The WinUI shell now exposes this local search through a small Cantonese-friendly query box; results are limited to the bounded local lexical service and inherit the withdrawn-Source privacy filter.
- The WinUI shell now exposes a participant-facing clarification panel for the latest transcript, with explicit person/place/relationship/date/event/identity/preference categories and separate speaker-confirmed, two-possibility, “唔記得”, and refusal outcomes. Original transcript revisions remain visible in the protocol and are never overwritten.
- The participant shell now exposes `NORMAL`, `PRIVATE_CONVERSATION`, and `LOCAL_CAPTURE_ONLY` as selectable modes for the next recording. Private and local-only modes disable cloud consent and current-information queries in the UI, while the persisted session policy remains authoritative in every service boundary.
- Capture start now persists cloud consent from the checkbox and selected privacy mode, so local recording never silently creates a granted cloud-consent event.
- Current-information queries now enforce the same privacy mode and explicit cloud-consent boundary in `CurrentInformationService`, so non-UI callers cannot bypass the participant guard.
- M04 now includes a complete non-sensitive synthetic corpus covering every required language-validation category; the corpus is wired through the report harness and remains clearly separate from real-provider quality evidence.
- The shell also exposes a local index repair action, so a health-check parity finding can be fixed from the app without opening SQLite or a terminal.
- Archive health checks now detect semantic search-index corruption (content, Source, Session, duplicates, and unexpected IDs) in addition to missing-row parity.
- The cumulative suite now passes **151/151** tests. These milestones are **IMPLEMENTED / AUTOMATED TESTED**, while target-machine restart/power-loss observation, provider extraction quality, and Family Admin review remain future verification work. See [M06–M09 evidence](evidence/M06-M09.md).
- Clarification provenance now rejects cross-session or unpersisted initial revisions before creating a correction chain.
- Clarification ownership checks now reject sessionless or mismatched Source context before creating a corrected revision, avoiding orphaned correction records.
- Clarification chain persistence now uses one SQLite transaction for corrected revision, event, vocabulary, and provenance search row.
- The WinUI capture flow now creates a participant Turn before opening the microphone, links the finalized Source and provider work to that Turn, and closes the Turn on normal stop, capture failure, shutdown recovery, or microphone-start failure. `ArchiveRepository` also persists turn closure and provides a restart-safe next sequence number.
- In-flight transcription, response, and speech-output calls now re-check Source withdrawal and cloud consent before writing success metadata, persisting derived speech, or handing audio to playback; deterministic withdrawal/consent race tests cover these boundaries.
- OpenAI response and extraction requests label transcript text as untrusted participant data and explicitly prohibit embedded commands from changing privacy or memory authority. Wrapper markers inside participant text are neutralized so transcript data cannot close the boundary and inject a new instruction block.
- Archive repository writes now enforce matching Source, Turn, Session, and transcript-revision context when those records already exist; foreign keys still handle missing-ID relationships.
- Archive writes now require an exact Source-to-Turn match when a Source is already turn-linked; transcript revisions and queued jobs cannot silently drop that provenance by supplying a null TurnId.
- Archive export now snapshots SQLite first and derives JSONL/media metadata from that same snapshot, preventing concurrent retry writes from producing a mixed-time bundle.
- `PRIVATE_CONVERSATION` is enforced as a cloud-blocked mode across bounded conversation, durable transcription/response, speech-output, and memory-extraction paths. Requests whose privacy mode does not match the persisted session are rejected before provider work begins; regression tests cover consent mistakenly recorded for a private session.
- Automatic extraction jobs are no longer queued for private or local-only sessions, so a privacy-blocked mode does not accumulate work that can never be permitted.
- Memory extraction also verifies that the caller-supplied session privacy mode matches the persisted session before invoking a provider, preventing a forged in-memory session from bypassing the private boundary.

## M10–M13 implementation attempt — 2026-09-13

- M10 added an `ISearchProvider` boundary and an external-information result that carries provider, retrieval time, source URLs, and an explicit untrusted flag. `OpenAiWebSearchProvider` now sends an allowlisted Responses web-search request with `store=false`, filters returned URLs to HTTPS hosts in the same allowlist, and never exposes results to the archive mutation path.
- External source filtering also rejects user-info URLs and non-default HTTPS ports, keeping displayed citations within the intended allowlisted web surface.
- The WinUI shell now exposes a Cantonese-friendly current-information query for allowlisted weather, public-service, transport, and news domains. It requires the explicit cloud-consent control, blocks `LOCAL_CAPTURE_ONLY` sessions, displays returned sources as untrusted, and never writes the result into archive memory.
- M11 added an authenticated-admin boundary, candidate claim review listing, attributed annotations, and explicit family assessment/admin rejection transitions. The repository does not treat family support as speaker confirmation.
- Family Admin review now displays linked Evidence relationships, participant certainty, and speaker-confirmation state, and an authorized administrator can play the latest finalized PCM WAV Source only after length, SHA-256, and WAV validation.
- Export, encrypted backup, and restore-verification controls now use the same Family Admin authorization and record minimal archive-operation audit metadata without paths or personal content.
- Added a Windows-only `WindowsAdministratorAuthorizer` that binds the actor ID to the current account and requires the Windows Administrators role; the fixed authorizer remains test-only and no automatic family allowlist is inferred. The WinUI shell now exposes a simple candidate review dialog with attributed support/rejection actions.
- M12 added self-contained JSONL table exports, an SQLite snapshot, optional collision-safe media copies, per-file SHA-256 manifest entries, and password-based AES-GCM backup/restore. File and bundle re-encryption now supports explicit password rotation without exposing plaintext beyond a temporary local staging path. Repeated exports get unique directories and do not overwrite an earlier snapshot.
- M13 added an archive health check for SQLite integrity, schema version, recoverable audio, due conversation jobs, tampered/missing finalized source or derived speech assets, and search-index row parity; authenticated source-scoped deletion now removes dependent content, including unambiguous session-level provider/derived assets, attempts media removal, and leaves a minimal tombstone; security scans and package vulnerability checks remain clean.
- Encrypted restore rejects duplicate archive entry names before extraction, preventing ambiguous overwrite semantics in a crafted backup.
- Encrypted restore also rejects distinct ZIP entry names that normalize to the same output path, such as a `nested/../archive.sqlite` alias.
- Restore manifest verification now hashes restored files as streams, avoiding whole-file memory loads for large recordings while retaining the same SHA-256 integrity check.
- Backup files now use chunked AES-GCM streaming (`MEMENTO2`) so large archives do not require whole-file memory allocations; `MEMENTO1` backups remain decryptable for compatibility.
- Backup restore now rejects altered chunk ciphertext and trailing bytes atomically, leaving no partial output.
- M13 now also supports an authenticated Source withdrawal policy: historical records and original media remain available locally, while queued/future cloud transcription and extraction, local search hits, and default exports exclude the withdrawn Source; retryable jobs are terminally blocked, and a minimal withdrawal annotation records the actor and reason. An explicit complete export can include withdrawn records for admin-controlled handling.
- Provider and retry-worker failure metadata now uses content-free summaries (error type, safe HTTP status, or generic network/timeout category), preventing custom provider messages or raw response bodies from entering the archive while preserving retry diagnostics.
- The WinUI shell now exposes the M12/M13 health-check, media export, encrypted-backup, and disposable restore/verification operations with plain Cantonese status messages; these actions still require supervised native UI verification.
- The Family Admin shell now exposes a confirmation-gated deletion of the latest finalized Source; it removes dependent content through the authenticated deletion service and preserves only a minimal audit tombstone.
- The Family Admin shell also exposes a confirmation-gated withdrawal of the latest finalized Source; it retains local history and media but disables future cloud processing and ordinary search/export paths.
- The cumulative suite now passes **151/151** tests. M10–M13 are **IMPLEMENTED / AUTOMATED TESTED**, with live search, live extraction quality, supervised Family Admin UX, target-machine encrypted bundle restore, and destructive reliability drills still pending. See [M10–M13 evidence](evidence/M10-M13.md).

## M14 status

- Real-user pilot work has not started. A supervised checklist is documented in [PILOT_RUNBOOK.md](PILOT_RUNBOOK.md), and the deployment evidence is recorded in [M14 evidence](evidence/M14.md); the app still requires consent, microphone, provider, admin, export/restore, and incident/rollback review before pilot use.
- A publish script now produces a self-contained `artifacts/MEMENTO-win-x64.zip` plus a SHA-256 sidecar; the optional MSIX staging/signing script remains separately gated by publisher identity, certificate, SDK tooling, and package validation.
- Added `packaging/Package.appxmanifest.template.xml` and `scripts/Build-MementoMsix.ps1`. The script parameterizes publisher/version, stages the self-contained app, calls Windows SDK `makeappx`, and optionally signs with `signtool`; it refuses missing tools/certificates and labels unsigned output clearly.
- `scripts/Install-Memento.ps1` now installs the portable bundle per user under `%LOCALAPPDATA%\\MEMENTO\\App`, creates a Start Menu shortcut, and registers a current-user Windows Installed apps entry; it does not claim signed package identity.
- `scripts/Start-Memento.ps1` now resolves the installed executable first and otherwise starts the repository publish output, making the supported launch path explicit.
- `scripts/Uninstall-Memento.ps1` removes that app registration and Start Menu shortcut, then schedules guarded app-tree cleanup after the script exits; it preserves `%LOCALAPPDATA%\\MEMENTO` archive data unless `-RemoveData` is explicitly passed.
- Installer updates now stage a clean app tree beside the install directory and swap it into place, so removed files cannot survive an update; a failed swap restores the previous app tree while leaving archive data untouched.
- Post-swap installer failures, including Start Menu shortcut creation errors, now move the failed tree aside and restore the previous app tree before cleanup; this rollback path was exercised against a disposable install root.
- Installer directory swaps retry short-lived Windows file locks before failing; the retry path was parser-checked and exercised with an isolated install root.
- The published bundle has been installed and launch-smoke-tested from `%LOCALAPPDATA%\\MEMENTO\\App`; the Start Menu shortcut exists and the app remains installed for supervised GUI/microphone verification.
- The optional application lock stores a salted PBKDF2 verifier in the current Windows user's Credential Manager, blocks the participant shell and retry worker while locked, and provides configure, disable, lock-now, and unlock flows. Pure hashing and live Credential Manager round-trip tests pass; target-machine recovery/policy review remains open.
- Added `scripts/Reset-MementoApplicationLock.ps1` as an explicit forgotten-passcode recovery path. It requires MEMENTO to be closed, removes only the current user's `MEMENTO/AppLock` credential, supports `-WhatIf`, and leaves archive data untouched.
- The installer now copies that recovery helper into the app-local tree alongside the uninstaller, so a moved or deleted repository does not remove the documented recovery path.
- A persistent `recording_enabled` setting now gives the participant an explicit enable/disable control independent of per-session consent.
- Added a read-only `Test-MementoPreflight.ps1` command for bundle checksum, installed executable, shortcut, archive path, free-space, process-state, and Credential Manager target checks; the cloud credential is a warning by default and becomes blocking with `-RequireCloudCredential`, while GUI, microphone, live exchange, and participant checks remain supervised.
- The same preflight can now require the optional application-lock verifier with `-RequireApplicationLock`, so a family policy can make lock setup a blocking deployment check without exposing the stored verifier.
- Added `scripts/Setup-Memento.ps1` as the one-command publish/install/preflight workflow, with `-SkipPublish`, `-RequireCloudCredential`, `-RequireApplicationLock`, and `-Launch` options for explicit deployment policy.
- The WinUI shell now locks privacy, recording, and cloud-consent controls while bounded cloud processing is in flight, preventing a participant from changing policy mid-turn.
- Active capture markers now update their WAV data length after each append, and recovery scanning can recover complete PCM frames left behind by a stale header after interruption.
- Optional TTS now completes the turn-based pipeline as local WAV → transcription → text response → verified derived speech storage → optional NAudio WAV playback. The WinUI shell can replay the latest stored assistant output; real output-device playback and continuous Realtime/WebRTC device streaming remain deployment work.
- The optional Realtime shell path now has a separate `LiveCloudConversation` consent checkbox and action. Realtime PCM output is converted to a verified 24 kHz mono WAV in `derived_speech_assets`, so the existing local playback and integrity checks can be used without treating provider audio as participant evidence.
- Added a cancellable device-chunk Realtime session boundary. When the separate live consent is selected, the shell opens the session before capture, forwards locally-written PCM chunks with 48→24 kHz downsampling, commits after stop, and preserves the local Source if connection or provider work fails. Continuous live transport is now implemented and tested; credential-broker, target-device, and live-account verification remain open.
- If a Realtime turn fails after local capture, the shell now queues the finalized Source for the existing durable transcription worker when the broader cloud-transcription consent is also granted; duplicate or already-transcribed work is skipped.

## Next action

Run the built app on target Windows hardware with native GUI observation and a real microphone, then verify start/stop, permission failure, recovery after interruption, the local Source record, live provider choice, admin authentication, and export/restore. Keep the current partial/automated status until those gates have real evidence; M14 pilot work should begin only after supervised review.
