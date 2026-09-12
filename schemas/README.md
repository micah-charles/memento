# Versioned data contracts

The schemas here are documentation-level contracts for M00.1 review. They are not yet wired to an application. The archive format must remain exportable as JSON/JSONL plus ordinary media files, with `schema_version` carried on every durable record.

## Domain records

MEMENTO deliberately separates the durable layers:

| Schema | Represents | Stable identifier |
|---|---|---|
| [source-record.schema.json](source-record.schema.json) | Original preserved material such as audio | `source_id` |
| [evidence-record.schema.json](evidence-record.schema.json) | A meaningful observation grounded in a Source | `evidence_id` |
| [memory-claim.schema.json](memory-claim.schema.json) | A structured proposition aggregated from Evidence | `memory_claim_id` |
| [response-episode.schema.json](response-episode.schema.json) | An observed stimulus → response episode | `response_episode_id` |
| [annotation-record.schema.json](annotation-record.schema.json) | Attributed context, correction, assessment, or review | `annotation_id` |

`memory-record.schema.json` remains as a compatibility entry point whose `oneOf` references these separate contracts. It does not imply that all records are one generic memory/evidence object.

AI-generated summaries, candidate links, and derived behavioural patterns are annotations or derived records. They cannot become a Source and cannot replace original Evidence.
