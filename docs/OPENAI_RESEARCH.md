# OpenAI research for MEMENTO

**Milestone:** M00 — Research and Architecture Foundation  
**Status:** Current research snapshot; re-check before implementation  
**Research/access date:** 2026-09-12

**Targeted recheck:** 2026-09-14. The official model catalogue and the current GPT-5.6 Terra, GPT-Transcribe, GPT-Realtime-2.1 Mini, GPT-4o Mini TTS, Responses web-search, audio API, and Realtime VAD references were rechecked before the CX reset. The model identifiers, reviewed pricing assumptions, `web_search` domain-filter shape, and adapter endpoint choices below remain consistent; no uncontrolled provider experiment was justified without a credentialed live exchange.

## Evidence labels

- **Verified current capability** — stated by the official source reviewed on the date above.
- **Design recommendation** — MEMENTO’s conclusion based on the verified capability and product constraints.
- **Assumption** — an implementation or cost assumption that must be measured.
- **Unverified / future** — not established by the reviewed source; must not be promised to a participant.

## Realtime voice

**Verified current capability:** OpenAI’s Realtime reference describes low-latency multimodal communication over WebRTC, WebSocket, and SIP, including speech-to-speech audio input/output. The current model catalogue lists reasoning realtime models, including `gpt-realtime-2.1` and `gpt-realtime-2.1-mini`, with audio input/output and function calling. The model page describes `gpt-realtime-2.1-mini` as a lower-cost realtime voice option and lists support for WebRTC, WebSocket, and SIP.

**Design recommendation:** Start M03 with a provider adapter whose default candidate is `gpt-realtime-2.1-mini`, using a transport selected after a Windows prototype. Keep the model alias in configuration and capture the actual model ID/snapshot in every session. Do not couple the archive schema to Realtime event names.

**Boundary:** Realtime creates a useful live exchange but is not the durable evidence path. The client persists local input/output event metadata, audio, and later transcript revisions independently.

## Realtime turn detection and interruption — targeted recheck 2026-09-14

**Verified current capability:** The official Realtime VAD guide says VAD is enabled by default for supported speech-to-speech sessions and exposes `server_vad` and `semantic_vad`. `server_vad` supports `threshold`, `prefix_padding_ms`, and `silence_duration_ms`; `semantic_vad` supports an `eagerness` setting where `low` gives the speaker more time. In conversation mode the guide documents `create_response` and `interrupt_response`. The Realtime conversation guide documents `input_audio_buffer.speech_started` / `speech_stopped`, response cancellation, and WebSocket-side `conversation.item.truncate` because the client owns output playback.

**Design recommendation:** MEMENTO should evaluate `semantic_vad` with low eagerness for the older-Cantonese pilot, with an explicit manual end/stop fallback and visible state. It should not enable aggressive VAD solely from documentation. A WebSocket implementation must stop playback and truncate unplayed assistant audio on interruption, while preserving the participant Source and never treating assistant audio as human evidence.

**Current implementation decision:** The existing adapter remains push-to-talk (`turn_detection = null`) until a provider/model/device test proves the event sequence and output playback accounting. CX04 is therefore a documented implementation gate rather than a claimed feature. This avoids sending both automatic VAD responses and the current manual `commit`/`response.create` sequence on one session.

## Durable transcription

**Verified current capability:** The current `gpt-transcribe` model page describes high-accuracy speech-to-text for completed audio files, streamed file transcripts, and committed Realtime turns. It describes unstructured context, keyword hints, and multiple language hints for domain terms, multilingual audio, and code-switching, and lists a current price of `$0.0045` per transcription minute.

**Design recommendation:** Use a separate durable transcription job after local audio is closed and hashed. Send only when the active privacy mode allows it. Retain the raw provider result, then create local corrected revisions rather than editing the raw result.

## Cantonese, Mandarin, and code-switching

**Verified current capability:** The reviewed OpenAI sources establish multilingual audio and multiple language/context hints. They do not guarantee Hong Kong Cantonese accuracy, distinguish Cantonese as a separately supported language code in the material reviewed, or guarantee recognition of family names and colloquial Hong Kong expressions.

**Design recommendation:** Cantonese, Mandarin, mixed Cantonese/Mandarin/English speech, English insertions, Cantonese names, incomplete speech, and colloquial expressions are M04 empirical acceptance requirements. Build a consented/redacted test corpus and measure word/entity/correction outcomes. Treat language hints and personal vocabulary as useful context, not truth.

**Unverified / future:** Do not promise perfect Cantonese, automatic dialect identification, or reliable diarization until the target microphone, room, model, and test corpus demonstrate it.

## Responses, reasoning, tools, and structured output

**Verified current capability:** The Responses create reference describes text or image inputs, text or JSON outputs, custom function calls, and built-in tools such as web search and file search. It documents a `tools` parameter and a `text` configuration for plain text or structured JSON. The current GPT-5.6 Terra model page lists function calling, structured outputs, web search, and file search as supported.

**Design recommendation:** Use Responses for bounded post-turn tasks: memory candidate extraction, structured intent classification, summaries, and controlled tool-backed answers. Define a schema that requires source IDs, evidence class, certainty, and provenance. Use `store=false` where supported and send only selected local context.

## Current-information retrieval

**Verified current capability:** The Responses API reference documents a built-in web-search tool and an includable source list (`web_search_call.action.sources`).

**Design recommendation:** Route weather, news, transport, and other current-information intents through a `SearchProvider`. Persist the query, source URLs, retrieval time, and answer provenance as an external-answer record. Never treat an external answer as personal memory. Search content is untrusted and must not change system policy, credentials, or canonical memory.

## Audio output

**Verified current capability:** The speech endpoint accepts text and supports audio output formats including WAV, FLAC, Opus, AAC, and PCM. The current TTS model reference lists `gpt-4o-mini-tts` and a `$12` per 1M audio-token output price.

**Design recommendation:** For live conversation use Realtime audio where the chosen model passes the voice-quality and cost gate. If a turn-based fallback is needed, use a TTS adapter and retain generated assistant audio as derived output, never as evidence of the participant’s voice. MEMENTO does not clone the participant’s voice in MVP.

## Privacy and retention

**Verified current capability:** OpenAI’s data-controls guide says API data is not used to train or improve models unless the customer opts in. It documents default abuse-monitoring retention of up to 30 days, application-state retention for certain endpoints, and that `/v1/responses` data is stored for at least 30 days when `store=true` or default behaviour applies. The guide lists `/v1/audio/transcriptions` with no application-state retention and `/v1/realtime` with 30-day abuse-monitoring retention; it also describes Zero Data Retention as an approval-based organisational control with endpoint limitations.

**Design recommendation:** MEMENTO’s local archive is authoritative. Do not use OpenAI-hosted conversation state, Files, or Vector Stores as the permanent family archive. Set `store=false` where supported, avoid uploading more audio/text than needed, and document the actual account’s configuration before pilot. Never present OpenAI’s default API controls as a guarantee that no content is retained.

## Pricing snapshot

The official pages reviewed list:

| Capability/model | Price shown on reviewed page | Unit |
|---|---:|---|
| `gpt-realtime-2.1-mini` input audio | `$10` | per 1M audio tokens |
| `gpt-realtime-2.1-mini` output audio | `$20` | per 1M audio tokens |
| `gpt-transcribe` | `$0.0045` | per audio minute |
| `gpt-5.6-terra` input text | `$2` | per 1M text tokens |
| `gpt-5.6-terra` output text | `$12` | per 1M text tokens |
| `gpt-4o-mini-tts` output audio | `$12` | per 1M audio tokens |

Realtime audio prices are token-based rather than a fixed per-minute price on the reviewed model page. The illustrative minute conversion and monthly estimates are therefore assumptions in [COST_MODEL.md](COST_MODEL.md), not an OpenAI promise.

## Provider replacement

MEMENTO stores provider/model metadata as provenance, not as the identity of the person. A future provider or local engine may implement the same `ConversationProvider`, `TranscriptionProvider`, `ReasoningProvider`, `SearchProvider`, and `MemoryExtractionProvider` contracts. Model replacement must never require rewriting original audio or direct statements.

## Sources

- Models catalogue: <https://developers.openai.com/api/docs/models> — accessed 2026-09-12.
- Realtime API reference: <https://platform.openai.com/docs/api-reference/realtime?lang=javascript> — accessed 2026-09-12.
- GPT-Realtime-2.1: <https://developers.openai.com/api/docs/models/gpt-realtime-2.1> — accessed 2026-09-12.
- GPT-Realtime-2.1 Mini: <https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini> — accessed 2026-09-12.
- GPT-Transcribe: <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- Responses create: <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- GPT-5.6 Terra: <https://developers.openai.com/api/docs/models/gpt-5.6-terra> — accessed 2026-09-12.
- Speech endpoint: <https://developers.openai.com/api/reference/cli/resources/audio/subresources/speech/methods/create> — accessed 2026-09-12.
- OpenAI data controls: <https://developers.openai.com/api/docs/guides/your-data> — accessed 2026-09-12.
- Realtime getting started: <https://developers.openai.com/api/docs/guides/realtime> — accessed 2026-09-14.
- Realtime VAD: <https://developers.openai.com/api/docs/guides/realtime-vad> — accessed 2026-09-14.
- Realtime conversations: <https://developers.openai.com/api/docs/guides/realtime-conversations> — accessed 2026-09-14.
- Realtime WebSockets: <https://developers.openai.com/api/docs/guides/voice-websockets> — accessed 2026-09-14.
