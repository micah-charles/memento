# MEMENTO architecture

**Current implementation direction (2026-09-15):** [Companion architecture](MEMENTO_COMPANION_ARCHITECTURE.md) and [Luna handoff](LUNA_HANDOFF.md) supersede the historical API-first interaction assumptions below. The account-backed text bridge has live evidence; voice lifecycle, isolation enforcement and fresh deployment remain partly unverified. Temporary Mandarin TTS is owner-approved.

**Milestone:** M00.1 — Architecture and schema refinement
**Status:** Ready for architecture review
**Research baseline:** 2026-09-12

## Architectural stance

MEMENTO is a lightweight native Windows application with a local archive and cloud-assisted conversation. The first implementation should be one packaged desktop process with clear internal interfaces and background work queues, not a fleet of runtime services.

The archive of record is local SQLite plus ordinary filesystem media. Cloud models may generate responses, transcripts, summaries, classifications, candidate Evidence, candidate Memory Claims, or derived annotations, but they do not own the family’s durable state. The durable domain is explicitly separated into Source, Evidence, Memory Claim, Response Episode, and Annotation records.

```text
Participant
   │ voice / text
   ▼
WinUI 3 presentation
   │
Application coordinator ─── policy / consent / privacy mode
   │                 │
   │                 ├── Audio recorder ─── local immutable audio
   │                 ├── Conversation provider ─── live response
   │                 ├── Transcription provider ─── durable transcript
   │                 ├── Reasoning / extraction provider ─── candidates
   │                 ├── Search provider ─── current external information
   │                 └── Local retrieval ─── SQLite/FTS5 and provenance
   │
   └── Local archive writer ─── SQLite + manifests + export/backup
```

## Runtime boundaries

These are interfaces and trust boundaries, not mandatory network calls or processes.

### `ConversationProvider`

Responsible for interactive turn-taking: session setup, audio/text input, interruption handling, response audio/text, tool requests, errors, and usage metadata. The provider must not write canonical memories directly.

```text
start_session(context: ConversationContext) -> ConversationSession
send_input(session, InputTurn) -> stream<ConversationEvent>
stop_session(session) -> ProviderUsage
```

The application owns the session ID and persists the input/output events locally.

### `TranscriptionProvider`

Turns finalized local audio into a transcript with segments, time offsets, language hints, optional diarization, and provider/model metadata. A transcript is a derived interpretation of audio, never a replacement for it.

```text
transcribe(audio: AudioAsset, hints: TranscriptionHints) -> TranscriptRevision
```

The durable path should support retrying after a network failure and retaining the raw provider output before human corrections.

### `ReasoningProvider`

Produces a response, classification, summary, or structured candidate extraction from explicitly selected local context. It must receive a constrained context package and return machine-readable provenance for each candidate. It must not receive the entire archive by default.

```text
reason(request: ReasoningRequest) -> ReasoningResult
extract_candidates(context: ExtractionContext) -> CandidateSet
```

### `SearchProvider`

Handles current-information retrieval only when policy classifies a turn as requiring it. Search results are external, time-bound, and untrusted. The provider returns sources and retrieval timestamps; results are not personal memories unless the participant separately states a personal fact.

```text
search(query: SearchQuery) -> ExternalAnswer
```

### `MemoryExtractionProvider`

This boundary can be implemented by the same model request as reasoning when practical. It emits provisional derived Evidence-class records with type, uncertainty, Source spans, and suggested links to existing or candidate Memory Claims. A model-produced record is not original Evidence: it must point to Source-backed material and must not be treated as a direct observation. It must not treat an AI summary as a Source, and it has no authority to promote a candidate to speaker-confirmed or family-supported state.

### `StorageProvider`

Owns SQLite transactions, append-only revisions, immutable Source references, Evidence links, Memory Claim aggregation, Response Episode records, annotations, integrity hashes, recovery queue, schema migrations, local search indexes, and export/backup snapshots. These are conceptual boundaries for M00; they do not prescribe the M01 table layout.

```text
append_session(session) -> PersistedSession
append_source(source) -> SourceRecord
append_transcript_revision(revision) -> PersistedTranscript
append_evidence(evidence) -> EvidenceRecord
upsert_memory_claim(claim) -> MemoryClaim
link_evidence_to_claim(evidence_id, claim_id, relation) -> EvidenceClaimLink
append_response_episode(episode) -> ResponseEpisode
append_annotation(annotation) -> Annotation
export_archive(destination, policy) -> ExportManifest
```

The names above are illustrative domain operations, not implementation requirements. A provider result may propose a link; the application policy and storage layer validate IDs, relationship type, temporal scope, and authority before persistence.

## Domain write rules

The application, not a model, decides whether a record may be written and how it is labelled:

1. A finalized local audio asset is appended as a Source before cloud processing.
2. A transcript span or observed turn may produce Evidence linked to that Source.
3. A Memory Claim is an aggregation target and must retain one or more Evidence links.
4. Evidence-to-claim links may `supports`, `weakens`, `contradicts`, `clarifies`, `supersedes`, `narrows`, `broadens`, or `contextualises` a claim. Similarity alone is not sufficient for automatic merging.
5. A Response Episode must link stimulus and response Evidence. Its observed behaviours must say whether they are direct, quoted, tone-based, system-observed, or AI-interpreted.
6. An Annotation is separately attributed and append-only. It may add family context, dispute, correction, or assessment; it cannot rewrite the participant’s certainty or make a family action appear to be speaker confirmation.
7. `speaker_confirmation`, `family_assessment`, and `admin_annotation` are independent provenance concepts. A family administrator can support or dispute a claim, but cannot set `confirmed_by_speaker` unless the participant’s own confirming Evidence exists.
8. AI output can create provisional derived Evidence-class records, candidate claims, candidate links, or derived patterns. It cannot create original Evidence, cannot create a Source, and cannot directly write canonical family state.

### `ExportProvider`

Writes a self-contained, documented export: `manifest.json`, JSONL records, human-readable Markdown summaries where useful, transcript revisions, source references, media, and checksums. Export must remain readable without MEMENTO.

## Turn lifecycle and durability order

1. Show a clear recording indicator and verify the active consent/privacy mode.
2. Open a session and create a durable session row.
3. Write microphone PCM frames to a temporary file while the final file remains absent or clearly marked incomplete.
4. Stream only the permitted audio/text to the conversation provider.
5. On stop, crash recovery, or segment rotation, flush and close the audio file, compute SHA-256, atomically rename it into `/raw/audio/YYYY/MM/DD/`, and commit the audio asset metadata.
6. Persist the provider events and raw transcript revision in a SQLite transaction.
7. Queue durable transcription, extraction, and indexing work. Each job is retryable and idempotent.
8. Store candidate Evidence and candidate Memory Claims with their independent authority assessments. Only participant-grounded Evidence may change speaker confirmation; family/admin actions remain separately attributed and cannot rewrite participant certainty.

If the process or network fails, the local recording and recovery marker remain the source of truth. A failed provider job must not roll back the audio.

## Layered retrieval

The context builder should select, in order:

1. current turn and a small recent window;
2. recent conversation summary, clearly labelled as derived;
3. relevant direct Evidence and participant-confirmed interpretations;
4. Memory Claims with their supporting/weakening/contradicting Evidence links and temporal scope;
5. Response Episodes and personal vocabulary/entity corrections where relevant;
6. uncertain or contradictory records, explicitly labelled;
7. AI inferences only when needed and clearly marked;
8. current external search results, clearly separated from personal memory.

The complete archive is never sent by default. Retrieval must preserve Source, Evidence, Claim, and Episode IDs so a response or admin view can explain why a claim exists, which evidence supports it, what was family-annotated, and which tier a future reconstruction would use.

## Local storage layout

```text
MemoryOfAPerson/
├── data/memory.db
├── raw/audio/YYYY/MM/DD/<session-id>.wav
├── raw/audio/YYYY/MM/DD/<session-id>.sha256
├── derived/transcripts/<session-id>/<revision>.json
├── derived/indexes/              # rebuildable search/summary/pattern indexes
├── exports/
├── backups/
└── logs/                    # operational metadata only; no raw content by default
```

SQLite stores references and structured metadata rather than large audio blobs. SQLite WAL mode, explicit transactions, foreign-key enforcement, and the Online Backup API are the planned reliability primitives. Filesystem media and the database are backed up as a consistent set; copying only a live `.db` file is not an approved backup method.

## Language and clarification policy

OpenAI’s current transcription documentation describes multilingual audio, language hints, keyword hints, and code-switching support, but the sources reviewed do not establish a guarantee for Hong Kong Cantonese accuracy. Cantonese, Mandarin, mixed Cantonese/Mandarin/English, names, colloquialisms, and uncertainty therefore remain an empirical M04 validation gate.

The policy layer should use a `CLARIFICATION` intent when recognition confidence, competing entity candidates, or semantic coherence is poor. It should quote the uncertain recognition naturally, ask one short disambiguating question, retain both the initial recognition and the correction, and never silently normalize one name into another.

## Current-information boundary

If a turn requires weather, news, transport, or other current information, the conversation provider may invoke a search tool through the controlled `SearchProvider`. The response must carry source URLs and retrieval time. External text is untrusted input: it can inform a spoken answer but cannot mutate canonical memory, local policy, credentials, or system instructions.

## Offline degradation

`LOCAL_CAPTURE_ONLY` always remains available when local disk and microphone access work. If cloud processing fails during a normal session, MEMENTO should save the audio and a pending job, explain the problem simply, and offer retry later. If the user wants an immediate offline answer, the MVP may display “I saved this conversation and can process it when internet returns”; a local LLM is not required.

## Deployment recommendation

Use C#/.NET with WinUI 3 delivered through the Windows App SDK. Use Windows Core Audio/WASAPI for capture, with Media Foundation or a small well-audited native adapter for file finalization/codec work. Keep the domain/application contracts independent of WinUI and OpenAI so a future provider, local transcription engine, or different UI can replace an adapter.

The Windows App SDK and WinUI recommendation, audio boundary, SQLite boundary, and OpenAI split are recorded in [ADR-001](adr/ADR-001-windows-framework.md), [ADR-002](adr/ADR-002-local-storage.md), and [ADR-003](adr/ADR-003-openai-voice.md).

## What M00/M00.1 does not decide

M00/M00.1 does not lock the exact SDK version, model alias, API transport, microphone brand, vector index, diarization policy, SQLite table layout, claim-aggregation threshold, or installer channel. Those are implementation choices to be verified at the relevant milestone. The stable contracts are more important than freezing today’s vendor names.

## Research sources

- OpenAI Models: <https://developers.openai.com/api/docs/models> — accessed 2026-09-12.
- OpenAI Realtime API reference: <https://platform.openai.com/docs/api-reference/realtime?lang=javascript> — accessed 2026-09-12.
- OpenAI Responses create reference: <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- OpenAI GPT-Transcribe: <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- Microsoft Windows app development: <https://learn.microsoft.com/en-us/windows/apps/> — accessed 2026-09-12.
- Microsoft WinUI 3: <https://learn.microsoft.com/en-us/windows/apps/winui/winui3/> — accessed 2026-09-12.
- Microsoft Core Audio: <https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components> — accessed 2026-09-12.
- SQLite WAL: <https://www.sqlite.org/wal.html> — accessed 2026-09-12.
- SQLite backup API: <https://www.sqlite.org/backup.html> — accessed 2026-09-12.
