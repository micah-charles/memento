# MEMENTO cost model

**Milestone:** M00 — Research and Architecture Foundation  
**Status:** Planning estimate, not a bill  
**Pricing research/access date:** 2026-09-12  
**Currency:** USD; taxes, account minimums, network, storage, and search-tool charges excluded unless stated.

## Principles

Cost is a product constraint, not a reason to discard evidence. MEMENTO should:

- measure audio minutes, input/output tokens, tool calls, retries, and extraction jobs;
- avoid sending the full historical archive on every turn;
- use short recent context plus relevant retrieved evidence;
- make memory extraction asynchronous and retryable;
- provide a local-capture-only path when cost or privacy matters;
- re-check model prices before each milestone and store actual usage receipts locally.

## Current prices used

The official model pages reviewed on 2026-09-12 show:

| Item | Price |
|---|---:|
| `gpt-realtime-2.1-mini` audio input | $10 / 1M audio tokens |
| `gpt-realtime-2.1-mini` audio output | $20 / 1M audio tokens |
| `gpt-transcribe` | $0.0045 / transcription audio minute |
| `gpt-5.6-terra` text input | $2 / 1M tokens |
| `gpt-5.6-terra` text output | $12 / 1M tokens |
| `gpt-4o-mini-tts` audio output | $12 / 1M audio tokens |

See [OPENAI_RESEARCH.md](OPENAI_RESEARCH.md) for source links and capability notes.

## Illustrative interactive-voice estimate

OpenAI publishes Realtime audio prices per audio token, not a universal per-minute conversion. Until M03 measures real usage, this model uses an explicit planning assumption:

- 1 minute of participant-visible conversation produces **6,000 input audio tokens**;
- the assistant speaks for a third of the minute, producing **2,000 output audio tokens**;
- durable transcription runs once per recorded minute at `$0.0045`;
- bounded text reasoning/extraction uses 1,000 input and 300 output text tokens per minute at GPT-5.6 Terra rates: `$0.0056/minute`;
- no retries, long-context premium, or web-search charge is included;
- a 25% operating buffer is shown separately for retries, longer answers, and tool use.

Under those assumptions:

```text
Realtime audio = (6,000 × $10 / 1,000,000) + (2,000 × $20 / 1,000,000)
               = $0.10 per conversation minute
Transcription = $0.0045 per minute
Text work     = (1,000 × $2 / 1,000,000) + (300 × $12 / 1,000,000)
               = $0.0056 per minute
Base planning rate = $0.1101 per minute
```

| Daily conversation | Minutes/month (30 days) | Base monthly estimate | With 25% operating buffer |
|---:|---:|---:|---:|
| 15 min/day | 450 | $49.55 | $61.94 |
| 30 min/day | 900 | $99.09 | $123.86 |
| 60 min/day | 1,800 | $198.18 | $247.73 |

These are deliberately transparent estimates, not a guarantee. Output speech ratio, model tokenisation, conversation context, interruptions, retries, current-information tool calls, and pricing changes can move the result substantially. M03 must replace the assumptions with recorded usage from representative Cantonese/mixed-language sessions.

## Archive-first asynchronous estimate

When the participant only records locally or uses a text-only/private path, the illustrative cloud floor is lower:

- durable transcription: `$0.0045/minute`;
- bounded extraction/text work: `$0.0056/minute`;
- combined illustrative rate: `$0.0101/minute`.

| Daily recorded minutes | Base monthly estimate |
|---:|---:|
| 15 | $4.55 |
| 30 | $9.09 |
| 60 | $18.18 |

This path does not provide a live spoken cloud reply. It exists to protect audio and keep the archive useful during privacy-sensitive or offline sessions.

## Storage and backup cost

M00 does not commit to a cloud-storage vendor or current disk price. At 48 kHz mono 24-bit PCM, the uncompressed payload is approximately 8.64 MB/minute before container metadata, so a 60-minute/day pattern is roughly 15.6 GB per 30 days of capture. Actual storage depends on channels, sample rate, bit depth, silence, derived FLAC copies, and retention policy. The app should report measured bytes and use a user-controlled retention/backup policy rather than hide storage growth.

## Cost controls

1. Prefer the smaller realtime model after language and response-quality tests pass.
2. Bound response length and context size.
3. Send recent and relevant evidence, not the whole archive.
4. Batch or defer extraction where the participant experience does not need immediacy.
5. Do not call web search for personal-memory questions.
6. Cache stable vocabulary and summaries locally, but keep source IDs.
7. Avoid repeated transcription of the same audio by hashing the input and making jobs idempotent.
8. Display monthly estimate and actual usage to Family Admin, not the primary participant.

## Sources and caveats

- GPT-Realtime-2.1 Mini pricing: <https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini> — accessed 2026-09-12.
- GPT-Transcribe pricing: <https://developers.openai.com/api/docs/models/gpt-transcribe> — accessed 2026-09-12.
- GPT-5.6 Terra pricing: <https://developers.openai.com/api/docs/models/gpt-5.6-terra> — accessed 2026-09-12.
- GPT-4o Mini TTS pricing: <https://developers.openai.com/api/docs/models/gpt-4o-mini-tts> — accessed 2026-09-12.
- OpenAI pricing page linked by the model pages: <https://developers.openai.com/api/docs/pricing> — accessed 2026-09-12; use the model pages and live account usage as the authoritative implementation check if the URL or catalogue changes.
