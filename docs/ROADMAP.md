# MEMENTO roadmap

**Status:** M00–M13 implementation is present; M14 pilot preparation and the CX conversation-experience reset are in progress; live and target-machine evidence gates remain explicit
**Last reviewed:** 2026-09-14

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
| M07 | Memory extraction | Structured candidate Evidence and candidate Memory Claims are produced asynchronously with schema validation; extraction never conflates the two or automatically promotes inference. |
| M08 | Provenance | Source → Evidence → Memory Claim is verifiable, with transcript revision, turn, session, audio span, checksum, relationship, and extraction metadata where applicable. |
| M09 | Vocabulary and entities | People, aliases, corrections, relationships, and personal vocabulary improve future context; entity resolution can link multiple Evidence records to one Claim without rewriting history. |
| M10 | Current-information tools | Weather/news/transport queries use an allowlisted search path with source URLs, retrieval time, untrusted-content handling, and no memory mutation. |
| M11 | Family Admin | Authenticated review of candidates, uncertainty, corrections, contradictions, audio playback, and audit events. |
| M12 | Export and backup | Self-contained JSONL/media export and encrypted, integrity-checked backup/restore work without proprietary software. |
| M13 | Reliability and security testing | Threat model, crash recovery, secret handling, prompt-injection, backup, update, and privacy tests pass. |
| M14 | Real-user pilot | One real participant uses the system safely with a documented support, consent, incident, and rollback plan. |

## Conversation Experience Reset (CX)

This is a product-facing track layered over the existing M01–M14 archive and security work. It must not weaken local-first Source finalization, consent, privacy modes, provenance, or Family Admin authorization.

| Checkpoint | Outcome | Current status |
|---|---|---|
| CX00 | Current-state audit of capture, Realtime, playback, persistence, and UI/admin coupling | PASS — see [CX00–CX02 evidence](evidence/CX00-CX02.md) |
| CX01 | Participant shell separated from Family Admin / diagnostics | PASS (automated) |
| CX02 | WinUI-independent participant conversation state machine | PASS — 5 deterministic tests |
| CX03 | Live voice loop with automatic derived-speech playback and safe return state | PARTIAL — auto-play wired; continuous microphone loop pending |
| CX04 | Verified turn detection / conservative VAD and interruption handling | BLOCKED pending official provider/device validation |
| CX05 | Conversational clarification | PARTIAL — durable protocol exists; spoken UX pending |
| CX06 | Bounded conversational memory continuity | PARTIAL — same-session context window implemented; claim-aware retrieval pending |
| CX07 | Spoken current-information routing | PARTIAL — conservative intent detector implemented; voice answer composition/live search pending |
| CX08 | Offline and provider-failure participant recovery | PARTIAL — local preservation and retry exist; visual supervised gate pending |
| CX09 | Windows UX validation | PENDING — native visual observation and physical I/O remain supervised gates |

Response Episode support is introduced incrementally: M07 may emit candidate episode links, M08 verifies stimulus/response provenance, M11 reviews episode labels and authority, and M12 exports episodes and supporting records. Derived behavioural patterns remain a later, rebuildable feature and must never be confused with observed episodes.

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

### M00.1 architecture-level acceptance scenarios

- **Multiple Evidence, one Claim:** three separate conversations about disliking heat remain three Sources and three Evidence records, while one possible Memory Claim links all three with `supports` relationships.
- **Temporal change:** a historical red colour preference and a current-as-of-2030 blue preference remain queryable claims with different temporal scopes; they are not silently treated as corruption.
- **Family support versus speaker confirmation:** a family member’s dated document may support an uncertain participant statement, but `speaker_confirmation` remains `none` until participant-grounded Evidence exists.
- **Response Episode:** a music-competition stimulus and the participant’s recorded praise/question become an observed Response Episode. “Praise + practical follow-up” is a derived pattern only, linked back to the Episode.

These tests are product requirements, not M00 implementation work.

## Exact next implementation step after review

After M00.1 architecture approval, build M01’s smallest vertical slice: a C#/.NET WinUI 3 shell with a local SQLite database, schema migration, session/turn/consent tables, and a fake in-process audio asset reference. M01 must not implement Source/Evidence/Claim extraction runtime beyond what is needed to prove the local schema boundary. Do not connect a cloud provider or capture real family audio until the M01 gate passes.

## Revisit triggers

Re-open the architecture if any of the following becomes true: a real-user language test fails materially; audio cannot be made crash-safe on target hardware; the chosen provider changes retention or voice transport; local archive size/backup cost becomes impractical; the participant needs multi-speaker use; or future simulation requirements pressure the system to blur evidence classes.
