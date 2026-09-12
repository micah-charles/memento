# MEMENTO memory and provenance model

**Milestone:** M00.1 — Architecture and schema refinement
**Status:** Ready for architecture review
**Schema baseline:** `1.1-draft`
**Research baseline:** 2026-09-12

> Evidence first. Interpretation second. Simulation last.

## The durable layers

MEMENTO does not treat “memory” and “evidence” as synonyms. Its canonical archive has separate layers:

```text
SOURCE (original preserved material)
   │
   ├── direct observation / transcript span
   ▼
EVIDENCE (what was said or observed)
   │                 │
   ├── supports ─────┘
   ├── weakens / contradicts / clarifies / contextualises
   ▼
MEMORY CLAIM (a structured proposition aggregated over time)

STIMULUS EVIDENCE ──┐
                     ▼
              RESPONSE EPISODE
                     │
                     ▼
       DERIVED BEHAVIOURAL PATTERN

ANNOTATION can add attributed context, correction, assessment, or review
to any layer; it cannot rewrite a Source or make an AI inference a direct statement.
```

### Source

A Source is original preserved material: primarily local audio in the MVP, and potentially video, photo, document, conversation-turn, or handwritten-letter material later. The source file and checksum are the archive’s durable anchor. A Source has a `source_id`, a source type, a path or external reference where applicable, a checksum where applicable, and capture/creation metadata.

Source time is distinct from the time described by the content. For example, an audio Source recorded in 2030 may contain a statement about an event in 1985.

AI output, summaries, embeddings, search results, and generated hypothetical answers are never Sources.

### Evidence

Evidence is a meaningful observation grounded in one or more Sources. A `direct_statement` means that the participant actually said or selected the recorded content. It does not mean that the underlying real-world proposition is objectively true.

Evidence retains the original expression, source ID, transcript revision/span, speaker where known, audio offsets where applicable, participant certainty, correction/contradiction links, and extraction metadata. The existing classes remain distinct and are not interchangeable:

| Evidence type | Meaning | Authority |
|---|---|---|
| `direct_statement` | The participant said or selected this content. | Evidence of what was said, not automatic proof of the proposition. |
| `confirmed_interpretation` | The participant explicitly confirmed an interpretation or correction. | Stronger participant-grounded interpretation, still linked to its confirming Evidence. |
| `ai_inference` | A model-generated hypothesis, summary, pattern, or interpretation retained as a derived record for audit. | Candidate/derived only; never original Evidence and never a Source. |
| `system_observation` | A technical observation such as capture completion or a checksum. | System metadata only. |
| `external_fact` | A time-bound observation from an external source such as weather or news. | Untrusted external information; no automatic personal-memory promotion. |

### Memory Claim

A Memory Claim is a structured proposition derived from one or more Evidence records. It may use a subject/predicate/object shape and a human-readable statement, but it must point back to its Evidence links. One claim can have many independent supporting, weakening, contradicting, clarifying, or contextualising Evidence records.

For example, five conversations about disliking very hot weather are five Sources and five Evidence records, but may resolve to one Memory Claim. The original statements remain independently retrievable and are never rewritten into the normalized claim.

Candidate Evidence must not be merged into a claim based only on semantic similarity. Entity identity, predicate/object meaning, temporal scope, and sufficient confidence must support aggregation. When uncertain, preserve separate candidates and let Family Admin review them.

An AI-produced candidate is not retroactively treated as original Evidence. A model may create a derived `ai_inference` record or candidate claim/link that points to Source-backed Evidence, but only the Source-backed record preserves what the participant actually said or what was directly observed.

### Response Episode

A Response Episode is a first-class observed domain object, not merely a future extension. It links:

1. stimulus and context Evidence;
2. the participant’s response Evidence;
3. observed linguistic or behavioural details;
4. optional follow-up Evidence; and
5. optional derived pattern IDs.

“Expressed excitement” can be an inference unless directly grounded in words, an explicitly observed tone, or another Evidence record. Each observed behaviour therefore carries a basis such as `direct_response`, `quoted_language`, `tone_observation`, or `ai_interpretation`.

Only observed episodes belong in this evidence-backed layer. A generated answer such as “Mother would probably say…” is simulation, not a Response Episode.

### Derived Pattern

A Derived Pattern is a later, rebuildable interpretation supported by multiple Response Episodes. For example, “when grandchildren achieve something, Mother often praises them and asks a practical follow-up question” is not a direct statement. It is an `ai_inference` or derived annotation that must link to the supporting episode IDs and expose its evidence tier during future reconstruction.

### Annotation

An Annotation is an attributed addition: family context, a dispute, a correction, a participant confirmation, an external-document assessment, a withdrawal, or a derived note. An annotation records who made it and what it targets. It does not mutate the original Source or Evidence.

## Confirmation authority

Confirmation is not one universal boolean. The model keeps independent assertions so the archive can show, for example:

```text
Participant said:       「好似係八二年掛」
Participant certainty:  uncertain
Family annotation:     tenancy agreement is dated 1982
Family assessment:      supported
Speaker confirmation:   none
External support:       document/reference linked
```

The three minimum concepts are distinct:

- `speaker_confirmation` / `confirmed_by_speaker`: only a participant-grounded confirmation or correction can set this. A family administrator cannot impersonate it.
- `family_assessment` / `confirmed_by_family`: a family member’s support, dispute, or context is valuable but remains separately attributed.
- `admin_annotation`: an authenticated administrator note, review, correction proposal, or dispute. It changes workflow state or adds context; it does not rewrite the participant’s certainty.

The schemas use separate `speaker_confirmation` and `family_assessment` objects, while `annotation_type` carries `admin_annotation`, `speaker_confirmation`, `family_assessment`, and related attributed actions. This is intentionally more expressive than one `status = confirmed` field.

AI model confidence is a separate numeric metadata field. It cannot change participant certainty, speaker confirmation, family assessment, or claim status by itself.

## Complete Source → Evidence → Claim example

These are three separate records in an export, shown together here to make the references explicit:

```json
{
  "source": {
    "schema_version": "1.1-draft",
    "record_type": "source",
    "source_id": "src_123",
    "source_type": "audio",
    "file_path": "raw/audio/2026/09/12/session123.wav",
    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "created_at": "2026-09-12T10:00:00Z",
    "recorded_at": "2026-09-12T10:00:00Z",
    "session_id": "sess_123",
    "turn_id": "turn_004",
    "immutable": true
  },
  "evidence": {
    "schema_version": "1.1-draft",
    "record_type": "evidence",
    "evidence_id": "ev_456",
    "evidence_type": "direct_statement",
    "statement": "我細個最鍾意食魚蛋",
    "original_expression": "我細個最鍾意食魚蛋",
    "subject_person_id": "person_mother",
    "speaker_id": "person_mother",
    "participant_certainty": "stated",
    "source_refs": [{
      "source_id": "src_123",
      "audio_start_ms": 12000,
      "audio_end_ms": 16400,
      "transcript_revision_id": "tr_123_r1",
      "transcript_span_id": "span_456"
    }],
    "created_at": "2026-09-12T10:01:00Z"
  },
  "memory_claim": {
    "schema_version": "1.1-draft",
    "record_type": "memory_claim",
    "memory_claim_id": "mc_789",
    "subject_person_id": "person_mother",
    "predicate": "likes_food",
    "object": "魚蛋",
    "statement": "Mother liked fish balls during childhood",
    "claim_status": "candidate",
    "evidence_links": [{ "evidence_id": "ev_456", "relation": "supports" }],
    "temporal_validity": {
      "scope": "childhood",
      "precision": "period",
      "temporal_expression": "細個",
      "certainty": "stated"
    },
    "speaker_confirmation": { "status": "none" },
    "family_assessment": { "status": "unknown" },
    "created_at": "2026-09-12T10:02:00Z"
  }
}
```

The outer object above is a documentation example containing three export records; each inner object is validated independently by its corresponding schema. `recorded_at` describes when the Source was captured, while the Claim’s `temporal_validity` describes when the proposition was true or intended to apply.

## Uncertainty, corrections, and contradictions

Participant certainty is preserved as `stated`, `uncertain`, `unknown`, or `not_applicable`. “好似係八二年掛” and “應該係八二年或者八三年” must keep their original expressions and uncertainty; the system must not silently choose 1982.

Corrections are append-only. For `阿珍 → 阿貞`, MEMENTO retains the original audio, initial transcript revision, corrected revision, correction Evidence/Annotation, and resolved vocabulary/entity link. A later confirmed correction can improve future context without mutating the old transcript.

Contradictory statements are retained as separate Evidence. Claim links may say `contradicts`, `clarifies`, or `supersedes` only when the relationship is sufficiently grounded. Retrieval must show the conflict and its time scope rather than silently overwrite history.

## Temporal validity

Every Memory Claim has a `temporal_validity` object. It can contain:

- `valid_from` and `valid_to` when dates are known;
- a human-readable `temporal_expression` such as 「細個嗰陣」, 「結婚之前」, or 「嗰幾年」;
- a flexible `scope` such as `childhood`, `before_marriage`, `2026`, `current`, `historical`, or `unknown`;
- `precision` such as `exact`, `year`, `period`, `fuzzy`, or `unknown`;
- `possible_years` for bounded alternatives; and
- temporal `certainty`.

`recorded_at` belongs to the Source/Evidence provenance chain. Event or claim time belongs to `temporal_validity`. A 2030 recording can therefore describe a 1985 event without conflating statement time and event time.

Historical and current states coexist. “I liked red in 2026” and “I prefer blue now” are queryable claims with different temporal scopes, not necessarily corruption or contradiction. Retrieval should distinguish historical preference, current preference, and unknown current state without deleting the older claim.

## Corrections and family support example

```text
Source src_001: audio recorded 2026-09-12
  └── Evidence ev_001: 「我哋好似1982年搬屋」
      evidence_type: direct_statement
      participant_certainty: uncertain
      speaker_confirmation: none
      └── Annotation an_001 by family_admin:
          “The tenancy agreement is dated 1982.”
          annotation_type: family_assessment
          assessment: supported
```

The family-supported assessment is not a `confirmed_by_speaker` event. An admin UI must keep those labels visible.

## Response Episode example

```text
Source src_020: family conversation audio
  ├── Evidence ev_100: stimulus — “Micah won a music competition.”
  └── Evidence ev_101: direct response — 「嘩！真係呀？好叻仔喎。有冇影相呀？」
       └── Response Episode re_001
           stimulus_evidence: ev_100
           response_evidence: ev_101
           observed behaviour: quoted praise + question about photos
           basis: quoted_language / direct_response
               └── optional Derived Pattern dp_001
                   “praise + practical follow-up question”
                   supported_by: re_001, re_088, re_231
                   tier: AI-derived behavioural pattern
```

The pattern is not direct evidence and cannot be stored as a hypothetical response episode.

## Retrieval questions this model supports

- What did Mother actually say about hot weather? → search Evidence and play linked Source spans.
- What Evidence supports the claim that Mother dislikes heat? → list all `supports` links for the Memory Claim.
- Has Mother’s preferred colour changed over time? → compare claim temporal scopes and their Evidence.
- How does Mother typically react to good news about grandchildren? → retrieve Response Episodes and only then derived patterns.
- What memories are uncertain? → filter participant certainty and temporal certainty.
- What has family annotated but Mother never personally confirmed? → compare `family_assessment` with `speaker_confirmation`.

The complete archive is never sent to a model by default. Retrieval preserves IDs and evidence tiers so an answer can explain its basis.

## Evidence tiers for future reconstruction

Future simulation is not an MVP feature, but its input contract must expose provenance:

1. Tier 1 — direct recorded response;
2. Tier 2 — participant-confirmed interpretation;
3. Tier 3 — Memory Claim strongly supported by Evidence;
4. Tier 4 — behavioural pattern supported by multiple Response Episodes;
5. Tier 5 — AI extrapolation;
6. Tier 6 — unknown.

A future answer should distinguish “She directly said something similar in three recorded conversations” from “This is an AI reconstruction based on related behaviour.”

## Export contract

The portable export should include, as applicable:

```text
sources.jsonl
evidence.jsonl
memory_claims.jsonl
response_episodes.jsonl
annotations.jsonl
people.jsonl
entities.jsonl
relationships.jsonl
conversations.jsonl
transcript_revisions.jsonl
vocabulary.jsonl
external_sources.jsonl
manifest.json
referenced media
```

Every durable record carries `schema_version` and a stable ID. A Memory Claim is reconstructable from its exported Evidence links; each Evidence record is traceable to exported Source material where policy permits. Privacy scope may omit or redact a Source while retaining a clearly marked unavailable reference.

The machine-readable entry point is [memory-record.schema.json](../schemas/memory-record.schema.json); the separate contracts are listed in [schemas/README.md](../schemas/README.md).

## M00.1 acceptance scenarios

### Multiple Evidence, one Claim

Three conversations produce three immutable Sources and three direct Evidence records:

```text
「我唔鍾意天氣太熱。」
「三十幾度我真係頂唔順。」
「我始終鍾意涼啲。」
```

The conceptual result is one possible Memory Claim “Mother dislikes very hot weather” with three `supports` links. The statements remain separate and searchable.

### Temporal change

```text
2026: 「我最鍾意紅色。」
2030: 「以前我鍾意紅色，而家反而鍾意藍色。」
```

The result keeps a historical red claim and a current-as-of-2030 blue claim. It does not treat the change as data corruption.

### Family support is not speaker confirmation

The 1982 moving-house example above retains `participant_certainty = uncertain`, family/external support, and `speaker_confirmation = none`.

### Response Episode

The music-competition example produces Source → response Evidence → observed Response Episode. “Praise + practical follow-up” remains a derived pattern, not a direct statement.

## M00.1 unresolved questions

- The minimum identity/meaning threshold for automatic claim aggregation needs real corpus evaluation in later milestones.
- The exact SQLite table layout and migration strategy are intentionally deferred to M01.
- Speaker diarization and reliable tone observation remain empirical implementation questions; tone must not be inferred from text alone.
- The user-facing vocabulary for “family supported” versus “confirmed” needs usability testing without collapsing authority levels.

## Research sources

- OpenAI GPT-Transcribe: <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- OpenAI Responses structured JSON/tool output reference: <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- SQLite FTS5: <https://www.sqlite.org/fts5.html> — accessed 2026-09-12.
