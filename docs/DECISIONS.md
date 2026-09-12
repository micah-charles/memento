# MEMENTO decisions

**Last updated:** 2026-09-12  
**Decision status:** Proposed until architecture review

| ID | Decision | Status | Record |
|---|---|---|---|
| D-001 | Use a native C#/.NET WinUI 3 application on Windows App SDK for the first platform. | Proposed | [ADR-001](adr/ADR-001-windows-framework.md) |
| D-002 | Use SQLite for structured local metadata and filesystem media for audio/photos/videos. | Proposed | [ADR-002](adr/ADR-002-local-storage.md) |
| D-003 | Use a Realtime provider adapter for live voice and a separate durable transcription path; candidate current models are configurable, not hard-coded into the archive. | Proposed | [ADR-003](adr/ADR-003-openai-voice.md) |
| D-004 | Treat provenance, evidence class, uncertainty, corrections, and contradictions as first-class immutable/revisioned data. | Proposed | [ADR-004](adr/ADR-004-memory-provenance.md) |
| D-005 | Keep the first runtime as one desktop process with internal boundaries and retryable background jobs. | Proposed | [ARCHITECTURE.md](ARCHITECTURE.md) |
| D-006 | Do not require a local LLM, GPU, voice cloning, avatar, fine-tuning, mobile app, elaborate knowledge graph, or cloud permanent memory database for MVP. | Proposed | [PRODUCT_VISION.md](PRODUCT_VISION.md) |
| D-007 | Make `NORMAL`, `PRIVATE_CONVERSATION`, and `LOCAL_CAPTURE_ONLY` explicit privacy modes. | Proposed | [PRIVACY_MODEL.md](PRIVACY_MODEL.md) |
| D-008 | Use FTS5 as the first local search index; any semantic index is derived and replaceable. | Proposed | [TECHNOLOGY_DECISIONS.md](TECHNOLOGY_DECISIONS.md) |
| D-009 | Treat current external information as untrusted, time-bound data with no authority to mutate family memory. | Proposed | [SECURITY_MODEL.md](SECURITY_MODEL.md) |
| D-010 | Separate Source, Evidence, Memory Claim, Response Episode, and Annotation into distinct durable concepts and portable record contracts. | Proposed | [ADR-005](adr/ADR-005-source-evidence-claim.md) |
| D-011 | Keep speaker confirmation, family assessment, and admin annotation as independent attributed assertions; family support cannot become speaker confirmation. | Proposed | [ADR-005](adr/ADR-005-source-evidence-claim.md) |
| D-012 | Give Memory Claims explicit temporal validity and preserve event time separately from Source recorded time. | Proposed | [ADR-005](adr/ADR-005-source-evidence-claim.md) |
| D-013 | Treat Response Episode as a first-class observed domain object; derived behavioural patterns must remain linked inference. | Proposed | [ADR-005](adr/ADR-005-source-evidence-claim.md) |

## Decision hygiene

An implementation milestone may turn a proposed decision into accepted, superseded, or rejected only with evidence, a dated note, and an ADR update. Model aliases, pricing, SDK versions, and legal guidance are time-sensitive and must be re-checked rather than inferred from this snapshot.
