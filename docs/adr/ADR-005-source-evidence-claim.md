# ADR-005: Separate Source, Evidence, Memory Claim, and Response Episode

**Status:** Proposed — awaiting M00.1 architecture review
**Date:** 2026-09-12

## Context

MEMENTO’s first M00 contract preserved provenance, but its single evidence-record shape could make an extracted memory look too similar to the material that supports it. A multi-decade archive must distinguish original material, what a person actually said or what was observed, normalized claims aggregated across conversations, and observed reactions that may later support behavioural reconstruction.

The archive must also preserve multiple authority levels. A participant’s uncertainty, a family member’s supporting context, an external document, and an administrator’s workflow annotation are not interchangeable confirmations.

## Options considered

1. Keep one generic memory/evidence record with a status or confidence field.
2. Build a broad knowledge graph with many ontology classes and relations.
3. Keep a small set of separate, versioned records: Source → Evidence → Memory Claim, with Response Episode and Annotation as first-class related records.

## Decision

Choose option 3. Use five documentation-level contracts:

- Source — original preserved material, immutable wherever possible;
- Evidence — a meaningful observation grounded in Source;
- Memory Claim — a structured proposition with one or more Evidence links;
- Response Episode — observed stimulus, participant response, and optional follow-up, all traceable to Evidence;
- Annotation — attributed context, assessment, correction, dispute, or review.

Memory Claims aggregate Evidence through explicit relationships such as `supports`, `weakens`, `contradicts`, `clarifies`, `supersedes`, `narrows`, `broadens`, and `contextualises`. Similarity alone must not merge records.

Keep `speaker_confirmation`, `family_assessment`, and `admin_annotation` independent. Only participant-grounded Evidence may establish speaker confirmation. A family administrator may support, dispute, contextualise, or annotate a claim, but cannot make an uncertain participant statement appear to have been confirmed by that participant.

Give each Memory Claim a flexible temporal-validity object with optional exact bounds, human time expressions, scope, precision, possible alternatives, and temporal certainty. Preserve Source `recorded_at` separately from event or claim time.

Response Episodes are observed records only. Generated hypothetical answers are not episodes. Derived behavioural patterns are rebuildable inferences linked to supporting episodes and must expose their evidence tier in any future reconstruction.

## Consequences

Positive: repeated Evidence can support one durable Claim without duplication; contradictions and changes over time remain visible; family authority cannot be confused with participant authority; response behaviour becomes usable for future reconstruction; exports remain vendor-neutral JSONL/media.

Negative: there are more IDs and relationships to display; claim aggregation needs cautious identity/meaning rules; a future admin UI must explain authority and temporal labels clearly.

M01 implementation impact is minimal. Implementation has not started, so the change affects the planned schema boundary and migration design, not an existing runtime. M01 remains a local WinUI + SQLite shell with session/turn/consent and a fake media reference; it does not start cloud processing, real capture, or extraction.

## Revisit conditions

Revisit the record shapes after corpus-based aggregation and usability tests. Do not add a universal ontology, RDF infrastructure, graph database, or microservice boundary without evidence that the small record set and SQLite archive cannot meet the product requirements.

## Sources

- [MEMENTO memory model](../MEMORY_MODEL.md) — project decision record, accessed 2026-09-12.
- [MEMENTO architecture](../ARCHITECTURE.md) — project boundary record, accessed 2026-09-12.
- [MEMENTO privacy model](../PRIVACY_MODEL.md) — layered privacy and authority, accessed 2026-09-12.
