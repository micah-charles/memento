# MEMENTO technology decisions

**Milestone:** M00 — Research and Architecture Foundation  
**Status:** Recommended baseline for review  
**Research baseline:** 2026-09-12

## Recommendation at a glance

| Area | Recommendation | Why | Not a commitment to |
|---|---|---|---|
| Windows UI | C#/.NET + WinUI 3 on Windows App SDK | Native Windows UX, accessible XAML, direct Windows API access, maintainable single-app deployment | A specific SDK version before M01 setup. |
| Audio capture | Windows Core Audio/WASAPI, PCM capture, atomic file finalization | Low-level control, resilient local capture, no lossy first write | One fixed device mix format; detect and record the actual format. |
| Archive | SQLite metadata + filesystem media | Durable, portable, transactional, easy to export and inspect | Embedding large media blobs in the database. |
| Search | SQLite FTS5 first; optional derived semantic index later | Local, rebuildable, low operational cost | A vector database in MVP. |
| Live AI | Provider interface with Realtime adapter; candidate `gpt-realtime-2.1-mini` | Natural low-latency voice with tool calling available on the current candidate | Permanent dependence on one model or transport. |
| Durable ASR | Provider interface with completed-audio transcription job; candidate `gpt-transcribe` | Separates live UX from archive-quality transcript and retry path | A claim of perfect Cantonese recognition. |
| Reasoning/extraction | Responses adapter with structured JSON and schema validation | Bounded context, tools, machine-readable candidate records | Automatic memory promotion. |
| Backup | SQLite Online Backup API plus media manifest/checksums, encrypted destination | Consistent snapshots and integrity verification | A cloud backup requirement. |

## Windows framework evaluation

### WinUI 3 + Windows App SDK — selected

Microsoft currently describes WinUI 3 as the recommended native UI framework for new Windows desktop applications and the Windows App SDK as the recommended development platform. It supports C#/.NET and XAML and brings Windows app lifecycle, windowing, deployment, and modern controls together.

The choice matches the first target: Windows-only, low-powered, accessible, simple, native, and audio/API integration heavy. It keeps the UI close to the OS without requiring a browser runtime.

### WPF — credible fallback

WPF is mature and familiar in .NET. It remains a reasonable fallback if the team’s controls, accessibility needs, or deployment constraints prove materially easier there. It is not selected for a new build because MEMENTO benefits from current Windows App SDK integration and a modern native surface.

### Tauri — future portability candidate

Tauri could reduce frontend portability cost, but it adds a webview/native boundary and does not remove the need for Windows audio, credential, packaging, and accessibility integration. It is not the first implementation choice while Windows is the only target.

### Electron — not selected

Electron provides a familiar web UI but carries a larger runtime and wider attack/dependency surface for a modest Windows application. The project’s main risk is evidence/reliability, not cross-platform UI speed.

## Audio decision

Capture through Windows Core Audio/WASAPI and preserve PCM before transcription or enhancement. Target a stable, lossless capture such as mono 48 kHz/24-bit PCM where the device and adapter support it; if not, record the device’s stable PCM mix format and store the actual technical metadata. Do not downsample, denoise, normalise, or encode the only copy before hashing and closing it.

Use a temporary file, flush/close it, hash it, and atomically rename it into the date/session directory. A derived FLAC copy may reduce storage while remaining lossless, but the original PCM/BWF-style master remains the evidence asset. 96 kHz/24-bit is a preservation-grade option, but it is not required for the speech MVP; the choice should be revisited if future voice-reconstruction research demonstrates a benefit.

## Storage and search decision

SQLite stores sessions, turns, transcript revisions, Sources, Evidence, Memory Claims, Evidence-to-Claim links, Response Episodes, annotations, people, entities, vocabulary, jobs, source URLs, and media metadata. Audio/photo/video files stay in the filesystem and are linked by immutable Source ID, relative path, checksum, format, duration, and capture metadata. The exact table layout and migration sequence remain an M01 implementation decision.

Enable foreign keys on every connection, use explicit transactions, and use WAL mode with planned checkpoint/recovery policy. Use FTS5 for lexical search over transcripts and Evidence, with IDs retained so a Claim or Response Episode can be explained. Add embeddings or a semantic index only as a rebuildable derived layer after lexical/provenance behaviour is proven.

## Open-source projects worth learning from

These projects are references, not dependencies selected for M00:

| Project | Verified purpose/license | What to learn | Caveat |
|---|---|---|---|
| [OpenAI Whisper](https://github.com/openai/whisper) | Multilingual speech recognition; MIT license shown in the repository | Offline/fallback ASR shape, model metadata, multilingual evaluation | Local inference is optional, CPU quality/latency and Cantonese performance must be measured. |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | C/C++ port of Whisper; MIT license shown in the repository | Windows/CPU-oriented deployment and low-level streaming options | It is a future fallback, not permission to require a local model/GPU. |
| [SQLite](https://github.com/sqlite/sqlite) | SQLite source is public domain; build scripts may have separate BSD-style terms | Durable single-file transactions and portable archive core | The application still owns schema, backups, encryption policy, and provenance. |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | Windows desktop framework; MIT license shown in the repository | Native Windows application patterns and packaging | Follow the specific SDK release and third-party notices used. |
| [Mem0](https://github.com/mem0ai/mem0) | AI memory layer; Apache-2.0 shown in the repository | Candidate extraction, update/retrieval patterns, memory evaluation ideas | Its default assumptions are not MEMENTO’s evidence chain; do not delegate canonical truth to it. |
| [Letta](https://github.com/letta-ai/letta) / [MemFS](https://github.com/letta-ai/letta-docs-md) | Stateful agents and git-backed Markdown memory; Apache-2.0 shown for Letta | Layered memory, explicit external memory, versioned context | Agent-owned memory and cloud options differ from a family-owned audio archive. |
| [FLAC](https://github.com/xiph/flac) | Lossless audio codec; library BSD-like license, tools/plugins have separate licenses | Lossless derived storage and validation | Check component licenses before shipping binaries. |

License status was read from the linked project pages on 2026-09-12; re-check at adoption and record dependency versions and notices.

## Deferred choices

- exact Windows App SDK/.NET target version;
- Realtime transport and client-secret/broker arrangement;
- final microphone format after device tests;
- optional diarization strategy;
- semantic search/index implementation;
- installer and update channel;
- local fallback transcription model;
- backup destination and key-recovery UX.

## Sources

- Windows app development: <https://learn.microsoft.com/en-us/windows/apps/> — accessed 2026-09-12.
- WinUI 3: <https://learn.microsoft.com/en-us/windows/apps/winui/winui3/> — accessed 2026-09-12.
- Windows Core Audio: <https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components> — accessed 2026-09-12.
- SQLite WAL: <https://www.sqlite.org/wal.html> — accessed 2026-09-12.
- SQLite foreign keys: <https://www.sqlite.org/foreignkeys.html> — accessed 2026-09-12.
- SQLite FTS5: <https://www.sqlite.org/fts5.html> — accessed 2026-09-12.
- SQLite backup API: <https://www.sqlite.org/backup.html> — accessed 2026-09-12.
- Library of Congress sound preservation: <https://www.loc.gov/programs/national-recording-preservation-plan/sound-preservation/> — accessed 2026-09-12.
- Library of Congress sound preferences: <https://www.loc.gov/preservation/digital/formats/content/sound_preferences.shtml> — accessed 2026-09-12.
- IETF FLAC standard: <https://www.rfc-editor.org/rfc/rfc9639.html> — accessed 2026-09-12.
