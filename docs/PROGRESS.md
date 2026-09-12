# MEMENTO progress

**Current milestone:** M00 — Research and Architecture Foundation  
**Status:** Documentation prepared; awaiting architecture review  
**Last updated:** 2026-09-12

## Completed in M00

- Confirmed the project scope from the master prompt: Windows-first, Cantonese-friendly, local family-owned archive, cloud-assisted conversation.
- Created the documentation-only repository structure and public-repository hygiene rules.
- Documented the product vision, architecture, provider boundaries, storage layout, privacy modes, evidence model, security threats, cost model, technology choices, roadmap, and ADRs.
- Defined the `1.0-draft` evidence record contract and a machine-readable JSON Schema.
- Researched current OpenAI realtime, transcription, Responses/tools, model, speech-output, privacy/retention, and pricing material using official sources.
- Researched Windows App SDK/WinUI, Core Audio/WASAPI, Credential Manager/DPAPI, SQLite transactions/WAL/FTS5/backup, archival audio, and reusable open-source projects using primary or authoritative sources.
- Encoded the three master-prompt acceptance scenarios and objective M01–M05 gates.

## Verification performed

- Required documentation paths exist.
- Markdown links point to repository paths or explicit source URLs.
- JSON Schema parses as JSON.
- No production code, API keys, recordings, transcripts, exports, or backups were created.
- Git remote setup and final status are recorded in [EVIDENCE.md](EVIDENCE.md).

## M00 exit criteria

- [x] Product vision is explicit about evidence first and non-goals.
- [x] Architecture defines conversation, transcription, reasoning, search, memory extraction, storage, and export boundaries.
- [x] Original audio, uncertainty, corrections, contradictions, and full provenance are defined.
- [x] Cantonese/mixed-language validation is treated as a measured gate, not an unsupported promise.
- [x] Privacy modes, consent, credential handling, deletion, backup, export, and offline degradation are documented.
- [x] Cost estimates cover 15/30/60 minutes per day with dated assumptions.
- [x] M01–M05 acceptance criteria and three master-prompt scenarios are documented.
- [x] Technology recommendation and exact next implementation step are documented.
- [ ] Human architecture review and approval.

## Next action

Pause production implementation. Review [ARCHITECTURE.md](ARCHITECTURE.md), [MEMORY_MODEL.md](MEMORY_MODEL.md), [PRIVACY_MODEL.md](PRIVACY_MODEL.md), and the four ADRs first. Once approved, start M01 exactly as described in [ROADMAP.md](ROADMAP.md).
