# MEMENTO roadmap

**Status:** M00 gate plan  
**Last reviewed:** 2026-09-12

Every milestone requires implementation, automated tests, manual verification, evidence, documentation, and a Git checkpoint. Passing compilation is not a gate. No milestone may weaken the evidence rules or privacy modes.

## Milestones

| Milestone | Outcome | Gate to continue |
|---|---|---|
| M00 | Research and architecture foundation | This repository is reviewed; decisions and open issues are accepted. |
| M01 | Windows shell and local storage | Native shell runs on target Windows hardware, creates a local DB, and passes schema/integrity tests without network access. |
| M02 | Reliable audio capture | Consent-visible PCM capture survives device changes, controlled crashes, and long sessions; every finalized asset has a checksum and recovery path. |
| M03 | Basic cloud voice conversation | A participant can have a bounded spoken conversation; the local archive remains authoritative; provider failures preserve audio. |
| M04 | Cantonese and mixed-language validation | A consented/redacted corpus measures Cantonese, Mandarin, mixed speech, names, English insertions, and uncertainty; the result is acceptable or scope is revised. |
| M05 | Clarification protocol | The “阿貞/阿珍” flow preserves initial recognition, clarification, correction, entity resolution, and provenance without silently normalizing. |
| M06 | Conversation persistence | The one-continuous-conversation UX maps to durable sessions/turns and supports offline queue/retry. |
| M07 | Memory extraction | Structured candidate memories are produced asynchronously with schema validation and no automatic promotion of inference. |
| M08 | Provenance | Every evidence record links to transcript revision, turn, session, audio span, checksum, and extraction metadata. |
| M09 | Vocabulary and entities | People, aliases, corrections, relationships, and personal vocabulary improve future context without rewriting history. |
| M10 | Current-information tools | Weather/news/transport queries use an allowlisted search path with source URLs, retrieval time, untrusted-content handling, and no memory mutation. |
| M11 | Family Admin | Authenticated review of candidates, uncertainty, corrections, contradictions, audio playback, and audit events. |
| M12 | Export and backup | Self-contained JSONL/media export and encrypted, integrity-checked backup/restore work without proprietary software. |
| M13 | Reliability and security testing | Threat model, crash recovery, secret handling, prompt-injection, backup, update, and privacy tests pass. |
| M14 | Real-user pilot | One real participant uses the system safely with a documented support, consent, incident, and rollback plan. |

## Objective M01–M05 acceptance criteria

### M01 — Windows shell and local storage

- A clean Windows install launches the native shell without network access.
- The app creates/migrates a local SQLite database and enables foreign keys on every connection.
- A session, turn, consent event, and placeholder media reference survive restart.
- `PRAGMA integrity_check` and migration tests pass on a fixture database.
- No real content is written to logs, source control, or test artifacts.

### M02 — Reliable audio capture

- Recording state is obvious, user-stoppable, and consent-gated.
- Capture writes PCM to a temporary file and atomically finalizes a dated/session-named asset.
- A controlled process termination at multiple points leaves either a valid finalized file or an explicitly recoverable partial asset; it never silently loses the captured bytes already flushed.
- SHA-256, byte length, format, duration, and session linkage are recorded.
- Device disconnect, disk-full, and insufficient-permission states produce a simple user message and an admin diagnostic.

### M03 — Basic cloud voice conversation

- A bounded spoken session produces a spoken response on a supported test machine.
- Input audio is stored locally before cloud transmission where the mode permits transmission.
- Provider events, model ID, usage, errors, and timestamps are persisted without raw content in operational logs.
- Network loss preserves local audio and creates a retryable job; the primary UI does not show a stack trace.
- Switching provider configuration does not require changing the archive schema.

### M04 — Cantonese and mixed-language validation

- The test corpus contains Cantonese, Mandarin, mixed Cantonese/Mandarin/English, names, incomplete speech, colloquial phrases, repetitions, and uncertainty.
- Word/entity recognition, clarification, latency, and correction outcomes are measured per category.
- The result records what is verified, weak, or unverified; no marketing claim of Cantonese quality is made from anecdotal examples.
- The system preserves the participant’s original language and does not translate away uncertainty by default.

### M05 — Clarification protocol

- The exact “阿貞/阿珍” scenario preserves original audio, initial recognition, participant correction, corrected transcript revision, resolved person/entity link, relationship, and complete provenance.
- “小三定小四” remains uncertain and is not resolved by model confidence or a guessed value.
- The same correction is available as future vocabulary context without mutating past evidence.
- If the participant declines to clarify, the record remains uncertain and the conversation continues gracefully.

## Future acceptance tests from the master prompt

1. **Correction/provenance:** initial misrecognition → natural clarification → speaker correction → preserved initial and corrected forms → entity/relationship/provenance update.
2. **Normal daily use:** current Hong Kong weather retrieval → natural answer → personal dislike of very hot weather may be stored as a direct statement, while today’s weather is not stored as personal memory.
3. **Memory discovery:** appropriate follow-up after a typhoon discussion → resulting story captured as evidence without forcing an interview.

These tests are product requirements, not M00 implementation work.

## Exact next implementation step after review

Build M01’s smallest vertical slice: a C#/.NET WinUI 3 shell with a local SQLite database, schema migration, session/turn/consent tables, and a fake in-process audio asset reference. Do not connect a cloud provider or capture real family audio until the M01 gate passes.

## Revisit triggers

Re-open the architecture if any of the following becomes true: a real-user language test fails materially; audio cannot be made crash-safe on target hardware; the chosen provider changes retention or voice transport; local archive size/backup cost becomes impractical; the participant needs multi-speaker use; or future simulation requirements pressure the system to blur evidence classes.
