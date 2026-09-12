# ADR-003: OpenAI voice and conversation integration

**Status:** Proposed — awaiting M00 architecture review  
**Date:** 2026-09-12

## Context

The product needs natural spoken conversation, clarification, current-information retrieval, and durable local evidence. Live voice latency and archive-quality transcription have different reliability/privacy/cost needs. OpenAI APIs and model aliases can change, so the archive cannot depend on provider-specific objects.

## Options considered

1. One Realtime speech-to-speech session for both conversation and archive.
2. Separate Realtime conversation plus durable transcription of the locally preserved audio.
3. Turn-based transcription → text reasoning → text-to-speech for all interaction.
4. Local-only speech/LLM processing.

## Decision

Use separate provider capabilities behind stable interfaces:

- Realtime adapter for the live exchange, with `gpt-realtime-2.1-mini` as the current candidate pending M03 tests;
- durable transcription adapter for closed local audio, with `gpt-transcribe` as the current candidate;
- Responses adapter for bounded structured extraction, reasoning, and web-search tool use;
- optional speech adapter for a turn-based fallback.

Persist local audio and session events independently. Set `store=false` where supported, do not use provider-hosted conversation state as the family archive, and capture provider/model/snapshot/usage metadata in local provenance. No local LLM, GPU, voice clone, avatar, or fine-tuning is required.

## Reasoning

The reviewed OpenAI documentation describes Realtime voice over WebRTC/WebSocket/SIP, current realtime models with audio input/output and tool calling, a high-accuracy completed-audio transcription model, and Responses structured JSON/tools/web search. Separate paths let live UX remain responsive while durable transcription is retryable and auditable. The model pages do not guarantee Hong Kong Cantonese quality, so M04 must measure it.

## Consequences

Positive: replaceable providers, durable local evidence, better failure isolation, bounded privacy flow, and a clear current-information boundary.  
Negative: more orchestration, duplicate processing in some sessions, and cost/latency that must be measured. Realtime audio pricing is token-based, so minute estimates are assumptions.

## Revisit conditions

Revisit after M03/M04 if a different provider materially improves target-language quality, if transport cannot meet Windows privacy/security requirements, if a turn-based flow is clearly better for the participant, or if measured cost is not sustainable.

## Sources

- <https://platform.openai.com/docs/api-reference/realtime?lang=javascript> — accessed 2026-09-12.
- <https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini> — accessed 2026-09-12.
- <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- <https://developers.openai.com/api/docs/guides/your-data> — accessed 2026-09-12.
