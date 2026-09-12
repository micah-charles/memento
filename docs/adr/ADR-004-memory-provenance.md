# ADR-004: Memory provenance and evidence integrity

**Status:** Proposed — awaiting M00.1 architecture review
**Date:** 2026-09-12

## Context

The purpose of MEMENTO is to preserve a real person’s memories and conversational behaviour without creating a “fake person.” Transcription errors, participant corrections, uncertain recollection, contradictions, summaries, and model inferences are inevitable. If the system stores them in one undifferentiated memory table, later users cannot tell what was actually said.

## Options considered

1. Mutable canonical facts with a confidence score.
2. Model-generated summaries as the memory database.
3. Append-only evidence/revision records with explicit provenance and derived indexes.

## Decision

Use append-only/revisioned Source and Evidence records with explicit Evidence type: `direct_statement`, `confirmed_interpretation`, `ai_inference`, `system_observation`, or `external_fact`. A separate Memory Claim aggregates one or more Evidence records through explicit relationships. Preserve original audio Sources, raw transcript revisions, corrected transcript revisions, correction annotations, contradictions, uncertainty, temporal expressions, and source spans. Every candidate must link to Source/Evidence IDs and to session, turn, transcript revision/span, audio asset/time range, and extraction/model metadata when applicable.

AI inference can be accepted or rejected as a workflow action by an explicit family-admin action, but it is never automatically promoted to a direct statement or speaker confirmation. Family support and admin annotation remain separately attributed. A summary or index is rebuildable and never replaces Source or Evidence.

## Reasoning

This is the product’s central ethical and engineering constraint. It makes “I don’t know” possible, keeps an old statement when a later recollection conflicts, supports future provider/model replacement, and lets a family member play the original audio.

## Consequences

Positive: auditability, correction without history loss, safer future reconstruction, one Claim supported by many Evidence records, and durable export.
Negative: more records and UI complexity, a required review workflow, careful retrieval/ranking so uncertainty, temporal change, and contradictions remain visible, and a future UI that must explain authority levels.

## Revisit conditions

Revisit only to extend the schema for new media/evidence classes or improve review UX. Never remove the provenance chain or collapse evidence classes for convenience.

## Sources

- [MEMENTO memory model](../MEMORY_MODEL.md) — project decision record, accessed 2026-09-12.
- [MEMENTO security model](../SECURITY_MODEL.md) — project threat model, accessed 2026-09-12.
