# MEMENTO memory and provenance model

**Milestone:** M00 — Research and Architecture Foundation  
**Status:** Proposed for architecture review  
**Schema baseline:** `1.0-draft`  
**Research baseline:** 2026-09-12

## Canonical evidence classes

MEMENTO stores evidence in separate classes. The classes are not interchangeable.

| Class | Meaning | Can become canonical fact automatically? |
|---|---|---:|
| `direct_statement` | The participant said or explicitly selected this content. | Yes, as a record of what was said; not as proof that the real-world claim is objectively true. |
| `confirmed_interpretation` | The participant confirmed a proposed interpretation or correction. | Yes, as a confirmed interpretation linked to the confirming turn. |
| `ai_inference` | A model-generated hypothesis, pattern, summary, or emotional interpretation. | No. |
| `system_observation` | A technical observation such as a session ending offline or an audio checksum. | Only as system metadata. |
| `external_fact` | A time-bound result from an external source such as weather or news. | No personal memory promotion. |

The distinction is visible in family-admin views and export files. A future reconstruction must be able to say whether an answer is based on a direct record, a confirmed interpretation, a retrieved external fact, a high-confidence pattern, an AI reconstruction, or unknown evidence.

## Record status and certainty

`certainty` describes the participant’s or record’s epistemic state, not the model’s confidence:

- `stated` — asserted without an uncertainty marker;
- `uncertain` — the participant used language such as “好似…掛” or offered alternatives;
- `confirmed_by_speaker` — the participant confirmed the interpretation or correction;
- `corrected_by_speaker` — a previous recognition or value was corrected;
- `contradictory` — another direct record conflicts with this one;
- `withdrawn` — the participant or authorised family administrator requested removal from active use.

A model confidence score, if available, is stored separately as `model_confidence` and never changes `certainty` by itself.

## Minimum provenance chain

Every memory candidate and every durable memory must be traceable through:

```text
memory/evidence record
  └── evidence span / transcript revision
        └── conversation turn
              └── session
                    └── original audio asset + SHA-256
```

The provenance record also includes extraction run, provider, model ID/snapshot, prompt/schema version, and timestamps. If a transcript is corrected, the old revision remains available and the correction is an annotation or a new revision.

## Candidate record shape

The initial contract is intentionally flexible: a record may use a subject/predicate/object triple, a free-form statement, or an event/reaction payload. It must still carry source and evidence fields.

```json
{
  "schema_version": "1.0-draft",
  "evidence_id": "ev_01H...",
  "subject_person_id": "person_mother",
  "record_type": "memory",
  "source_type": "direct_statement",
  "status": "candidate",
  "certainty": "uncertain",
  "statement": "好似係八二年掛",
  "subject": "mother",
  "predicate": "moved_house_year",
  "object": 1982,
  "original_expression": "好似係八二年掛",
  "session_id": "sess_01H...",
  "turn_id": "turn_01H...",
  "audio_asset_id": "aud_01H...",
  "audio_start_ms": 12340,
  "audio_end_ms": 16880,
  "transcript_revision_id": "tr_01H...r1",
  "transcript_span_id": "span_01H...",
  "extraction_run_id": null,
  "provider": null,
  "model_id": null,
  "model_snapshot": null,
  "prompt_version": null,
  "model_confidence": null,
  "created_at": "2026-09-12T00:00:00Z",
  "supersedes_evidence_id": null,
  "contradicts_evidence_ids": [],
  "notes": "Do not resolve Primary 3/Primary 4 or exact year without participant confirmation."
}
```

The same record may include `entities`, `relationships`, `event`, `reaction`, `personal_vocabulary`, or `sensitivity` payloads as schema extensions. Extensions must not remove provenance fields.

## Corrections

A correction is append-only metadata linked to both the original recognition and the correction turn.

Example:

```text
Initial recognition: 阿珍
Participant correction: 阿貞（貞潔個貞）
Resolution: confirmed_by_speaker
```

The system stores the initial transcript, corrected transcript revision, correction annotation, entity alias/canonical form, supporting audio spans, and the participant who made the correction. Future context may use the confirmed vocabulary entry, but the original error remains auditable.

## Contradictions and change over time

The archive does not overwrite an older direct statement when the participant later says something different. It records both statements and a relation such as `contradicts`, `clarifies`, or `supersedes` only when the relation is supported by the participant or a documented admin decision.

“We moved in 1978” and “Actually I think it was 1979” remain two historical records. Retrieval should present the conflict and uncertainty rather than select a single value without evidence.

## Summaries and indexes

Summaries, embeddings, search indexes, entity aliases, topic labels, and extracted patterns are derived indexes. They can be rebuilt from the evidence layer. They must carry the source IDs and generation metadata needed to invalidate or regenerate them.

No summary is allowed to replace the source transcript or original audio. No embedding is treated as a source of truth.

## Personal vocabulary

Vocabulary entries contain canonical spelling, aliases, language/pronunciation hints where useful, entity type, relationship, previous recognition errors, and confirmed corrections. Similar-sounding names are not merged automatically. A vocabulary item can improve a future provider prompt, but it cannot rewrite past evidence.

## Reactions and non-verbal evidence

The schema can later represent a reaction episode with stimulus, participant response, context, emotion as stated or cautiously inferred, and linked media. Only observed audio/video/photo evidence belongs in the direct evidence layer. A generated example answer is never a recorded reaction.

Photos, videos, pets, and other media use the same asset/provenance pattern but are outside MVP extraction scope.

## Three master-prompt acceptance scenarios

### A. Name correction and provenance

Input: 「我細個有個friend叫阿貞。」 The system initially recognises “阿珍,” asks naturally, receives “貞潔個貞，阿貞,” and must preserve audio, initial recognition, correction, resolved entity, relationship, and provenance. When the participant says “小學識㗎，好似小三定小四啦,” the record must retain Primary 3/Primary 4 as uncertain rather than choose one.

### B. Ordinary daily use and current information

Input: 「今日香港天氣點呀？」 The system retrieves current information and answers naturally. If the participant says 「我最憎三十幾度」, today’s temperature is an external fact, not a personal memory; the participant’s dislike of very hot weather may become a direct statement with provenance.

### C. Natural memory discovery

After an appropriate current answer about a typhoon, the companion may ask 「講起打風，你細個喺香港有冇邊次打風特別記得？」 The resulting story is captured because useful conversation opened a natural memory opportunity, not because the system forced an interview.

## Human review rule

AI extraction produces candidates. A family administrator can accept, reject, edit, annotate, or mark a candidate for review, but an edit must never rewrite the underlying direct statement. The UI must show what came from the person, what was model-derived, what is uncertain, and how to play the supporting audio.

## Export contract

The export includes `people.jsonl`, `entities.jsonl`, `relationships.jsonl`, `conversations.jsonl`, `transcript_revisions.jsonl`, `evidence.jsonl`, `annotations.jsonl`, `vocabulary.jsonl`, `external_sources.jsonl`, `manifest.json`, and the referenced media. Each JSONL record carries `schema_version`; each media item carries a checksum and relative path.

The draft machine-readable contract is [memory-record.schema.json](../schemas/memory-record.schema.json).

## Research sources

- OpenAI GPT-Transcribe: <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- OpenAI Responses structured JSON/tool output reference: <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- SQLite FTS5: <https://www.sqlite.org/fts5.html> — accessed 2026-09-12.
