# MEMENTO progress

**Current milestone:** M01 — Windows shell and local storage
**Status:** BLOCKED pending native-window manual verification
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
- M02–M05 are not started.

## Next action

Run the built M01 shell on target Windows hardware with native GUI observation, verify the visible participant surface and local application-data behaviour, then rerun the M01 gate. Do not begin M02 until that gate passes.
