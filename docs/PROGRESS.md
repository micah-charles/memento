# MEMENTO progress

**Current milestone:** M13 — Reliability and security foundations
**Status:** IMPLEMENTED / AUTOMATED TESTED; live provider, hardware, authenticated admin UX, and pilot gates remain pending
**Last updated:** 2026-09-13

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
- Added five automated tests for migration, restart persistence, foreign keys, source metadata, relationships, and integrity.
- Solution build succeeded and vulnerability scan is clean after pinning the SQLite native provider to 2.1.13.
- M01 is **BLOCKED**, not passed: the current execution surface cannot visually inspect a native Windows window or prove clean-install/manual shell behaviour. See [M01 evidence](evidence/M01.md).
- M01 remains blocked for the visual/manual gate because native-window observation is unavailable in this execution surface.

## M02 implementation attempt — 2026-09-13

- Added crash-safe PCM WAV capture under a date-organized local `raw/audio` directory.
- Capture writes a `.capture.tmp` file, flushes each append, validates the WAV, computes SHA-256, atomically renames the file, and only then registers finalized `SourceMetadata`.
- Added a provider-neutral `IAudioInput` boundary and a Windows NAudio `WaveInEvent` adapter. The UI now requires an explicit local-recording checkbox and exposes large start/stop controls.
- Added deterministic tests for consent gating, normal finalization, checksum and metadata registration, recoverable partial files, and corrupt partial preservation. These tests use a fake input; physical microphone, permission, disconnect, disk-full, and crash tests remain unverified.
- M02 is **PARTIAL/BLOCKED**, not passed: automated coverage is green, but physical microphone and native UI observation have not been verified in this environment. See [M02 evidence](evidence/M02.md).

## M03 implementation attempt — 2026-09-13

- Re-checked official OpenAI documentation on 2026-09-13. The current Realtime reference describes WebRTC/WebSocket/SIP audio sessions; the current model catalogue lists GPT-Transcribe for high-accuracy speech-to-text and GPT-Live-Transcribe for low-latency transcription. The archive therefore keeps transport, transcription, reasoning, and model identifiers behind provider-neutral contracts.
- Added `ConversationOrchestrator`, `IConversationProvider`, `ITranscriptionProvider`, and `IReasoningProvider` contracts. The orchestrator requires a finalized local audio path and explicit cloud consent before a provider call, and it refuses cloud processing in `LOCAL_CAPTURE_ONLY` mode.
- Added append-only `provider_interactions` metadata with provider, capability, model, snapshot, request ID, timestamps, usage, success, and redacted error fields. Provider failures are recorded without deleting or changing local audio.
- Added a deterministic provider for offline contract tests. No OpenAI credential or live network call is present in the repository.
- M03 is **PARTIAL/BLOCKED**: 14 automated tests pass at that checkpoint, but a live bounded voice interaction and target-machine microphone path remain unverified. See [M03 evidence](evidence/M03.md).

## M04 implementation attempt — 2026-09-13

- Added a reproducible language-validation harness with explicit Hong Kong Cantonese, colloquial Cantonese, Mandarin, mixed-language, names, places, dates, numbers, hesitations, repetitions, uncertainty, and product-name categories.
- The harness reports transcript similarity, entity/name accuracy, code-switch preservation, uncertainty preservation, latency, and a case-level disposition (`PASS`, `ACCEPTABLE_WITH_CLARIFICATION`, `WEAK`, `FAIL`, or `NOT_TESTED`). Name/entity accuracy is measured separately because a fluent sentence with a wrong person name is unsafe.
- Fixtures are synthetic and contain no family recordings or private transcripts.
- M04 is **PARTIAL/BLOCKED**: 18 deterministic tests pass cumulatively, but no real provider corpus or credentialed Cantonese measurement has been run. See [M04 evidence](evidence/M04.md).

## M05 implementation attempt — 2026-09-13

- Added append-only transcript revisions, clarification events, and speaker-confirmed vocabulary entries with foreign-key links to the original Source and session.
- Added a clarification policy that prioritises names, places, relationships, dates, identity, preferences, and other high-impact ambiguity while avoiding low-impact filler checks. Speaker correction text is stored verbatim and always outranks model confidence.
- The protocol supports confirmation, refusal, “唔記得”, two possibilities, and correction of a previous correction. A corrected revision never overwrites the initial recognition.
- M05 is **PARTIAL/BLOCKED**: 23 deterministic tests pass cumulatively, but participant UX and natural Cantonese turn-taking have not been observed. See [M05 evidence](evidence/M05.md).

## M06–M09 implementation attempt — 2026-09-13

- M06 added durable `conversation_jobs` with pending/processing/succeeded/failed states, attempt counts, retry timestamps, and error metadata. `ConversationSessionWriter` maps the continuous session UX onto durable sessions, turns, local Sources, and retryable work.
- M07 added candidate `EvidenceRecord`, `MemoryClaim`, and explicit `EvidenceClaimLink` records. `MemoryExtractionService` uses a deterministic provider boundary and never promotes candidates beyond `candidate` status.
- M08 added `ProvenanceGraph.Validate`, which checks Source → transcript revision → Evidence → Claim links, allowed relationships, and blocks an unconfirmed AI inference from silently becoming a reviewed claim.
- M09 added person entities, speaker-confirmed aliases, and Evidence-to-entity links. Alias history is additive and does not rewrite transcript or Source records.
- The cumulative suite now passes **27/27** tests. These milestones are **IMPLEMENTED / AUTOMATED TESTED**, while real offline restart workers, provider extraction quality, and Family Admin review remain future verification work. See [M06–M09 evidence](evidence/M06-M09.md).

## M10–M13 implementation attempt — 2026-09-13

- M10 added an `ISearchProvider` boundary and an external-information result that carries provider, retrieval time, source URLs, and an explicit untrusted flag. Search results have no archive mutation path.
- M11 added an authenticated-admin boundary, candidate claim review listing, attributed annotations, and explicit family assessment/admin rejection transitions. The repository does not treat family support as speaker confirmation.
- M12 added self-contained JSONL table exports, an SQLite snapshot, optional media copies, per-file SHA-256 manifest entries, and password-based AES-GCM backup/restore.
- M13 added an archive health check for SQLite integrity, schema version, recoverable audio, and due conversation jobs; security scans and package vulnerability checks remain clean.
- The cumulative suite now passes **31/31** tests. M10–M13 are **IMPLEMENTED / AUTOMATED TESTED**, with live search, OS-backed admin authentication, encrypted backup restore on target hardware, and destructive reliability drills still pending. See [M10–M13 evidence](evidence/M10-M13.md).

## M14 status

- Real-user pilot work has not started. The app still requires supervised consent, microphone, provider, admin, export/restore, and incident/rollback review before pilot use.

## Next action

Run the built app on target Windows hardware with native GUI observation and a real microphone, then verify start/stop, permission failure, recovery after interruption, the local Source record, live provider choice, admin authentication, and export/restore. Keep the current partial/automated status until those gates have real evidence; M14 pilot work should begin only after supervised review.
