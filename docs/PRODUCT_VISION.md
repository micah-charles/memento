# MEMENTO product vision

**Milestone:** M00 — Research and Architecture Foundation  
**Status:** Proposed for architecture review  
**Research baseline:** 2026-09-12

## Purpose

MEMENTO is a simple Windows conversation companion for an older family member. It should be pleasant enough to use in ordinary life while quietly preserving a high-quality, family-owned archive of memories, stories, preferences, relationships, speech, and observed conversational reactions.

The companion is not primarily a chatbot, an avatar, or a voice-cloning product. The durable product value is authentic human evidence that remains useful when models and providers change.

> Evidence first. Interpretation second. Simulation last.

## The participant experience

The participant should see one apparently continuous conversation rather than chat threads, databases, prompts, or model controls. The first screen should be calm, high-contrast, large-text, and task-light:

```text
早晨

今日想傾啲乜？

        🎙
     撳一下講嘢
```

The application should support natural Cantonese, Mandarin, Cantonese mixed with Mandarin, English words inside Cantonese, colloquial Hong Kong expressions, names spoken with Cantonese pronunciation, incomplete sentences, repetition, and uncertainty. It must ask for clarification instead of pretending to understand.

The companion should handle:

- casual conversation and everyday questions;
- current-information questions such as weather, news, transport, public information, and recipes when online retrieval is enabled;
- meaningful follow-up questions when a memory topic naturally appears;
- spoken responses and visible text status such as “Listening…”, “Thinking…”, “Speaking…”, “Didn’t quite understand…”, and “Internet unavailable.”

Memory discovery must feel like conversation, not an autobiography interview. A tired or uncomfortable participant should be allowed to stop without pressure.

## The family value

Family administrators may eventually inspect people, relationships, stories, events, places, preferences, corrections, uncertainty, contradictions, audio, backups, exports, and memories requiring review. The participant experience must not expose database concepts.

Every durable memory must be traceable to a conversation turn and, where it originated in speech, an exact range in the original local recording. A family member should eventually be able to select a memory and hear the supporting audio.

Direct statements, confirmed interpretations, and AI inferences are different evidence classes. An inference may help retrieval or suggest a question, but it never silently becomes a fact.

## Product promises

1. The family owns the long-term archive locally.
2. The original recording is preserved before destructive processing.
3. Uncertainty, corrections, contradictions, and provenance are retained rather than “cleaned up.”
4. Cloud AI is an interchangeable capability, not the archive of record.
5. Loss of internet connectivity must not lose a recording or silently discard a session.
6. Recording is obvious and consent is explicit.
7. Current external information is clearly distinct from personal memory and is not automatically stored as personal memory.
8. Export is designed from the beginning so the family can leave MEMENTO.

## Explicit non-goals

MVP and M00 do not require:

- a local LLM or GPU;
- voice cloning, fine-tuning, an avatar, or a 3D character;
- a mobile application;
- an elaborate knowledge graph;
- a social network or multi-family cloud service;
- a cloud-hosted permanent family-memory database;
- an automated claim that a future reconstruction literally is the person.

## Success definition for the first pilot

A real participant can open MEMENTO, press one obvious control, speak naturally for an ordinary session, receive a useful response, and stop without technical help. The session survives an internet interruption; the local recording and transcript are recoverable; important memories can be reviewed with their evidence; and a family administrator can export the archive.

The first pilot is successful only if it demonstrates epistemic honesty as well as conversational usefulness. A fluent demo that loses audio, invents a correction, or turns an AI guess into a fact is a failure.
