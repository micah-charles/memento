# MEMENTO evidence register

**Research/access date:** 2026-09-12  
**Scope:** M00 documentation and architecture only

## Repository evidence

| Check | Result |
|---|---|
| Working directory | `/Volumes/ExtremePro/AIWorkspace/MEMENTO` |
| Initial state | Empty directory; no pre-existing application or user files found. |
| Deliverables | Required README, docs, ADRs, schema, and reserved-directory READMEs created. |
| Production runtime | Not created. |
| Personal data | No real audio, transcript, export, backup, or credential created. |
| Network/API calls | No OpenAI API call made; research used public documentation pages only. |
| Git push | Not performed. |

## Source register

Current or time-sensitive claims should be re-checked before implementation. The main documents repeat the source URL next to the relevant decision; this register provides the minimum source set used for M00.

### OpenAI

| Topic | Source | Used for |
|---|---|---|
| Model catalogue | <https://developers.openai.com/api/docs/models> | Current model families, multilingual framing, realtime/audio categories. |
| Realtime API | <https://platform.openai.com/docs/api-reference/realtime?lang=javascript> | WebRTC/WebSocket/SIP and audio session boundary. |
| Realtime 2.1 Mini | <https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini> | Candidate live model capabilities and audio pricing. |
| GPT-Transcribe | <https://developers.openai.com/api/docs/models/gpt-transcribe> | Durable transcription, code-switching hints, per-minute price. |
| Responses create | <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> | JSON output, tools, web search, `store`, source inclusion. |
| GPT-5.6 Terra | <https://developers.openai.com/api/docs/models/gpt-5.6-terra> | Candidate text reasoning/extraction pricing and structured outputs. |
| GPT-4o Mini TTS | <https://developers.openai.com/api/docs/models/gpt-4o-mini-tts> | Optional turn-based speech output and audio pricing. |
| Data controls | <https://developers.openai.com/api/docs/guides/your-data> | Training, abuse monitoring, application state, retention controls. |

### Windows and storage

| Topic | Source | Used for |
|---|---|---|
| Windows app platform | <https://learn.microsoft.com/en-us/windows/apps/> | WinUI 3/Windows App SDK recommendation and support baseline. |
| WinUI 3 | <https://learn.microsoft.com/en-us/windows/apps/winui/winui3/> | Native UI, C#/.NET/XAML, Windows desktop positioning. |
| Windows Core Audio | <https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components> | WASAPI/Core Audio capture boundary and shared/exclusive considerations. |
| Credential handling | <https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords> | Credential Manager, DPAPI, secret minimisation and logging rules. |
| SQLite transactions | <https://www.sqlite.org/atomiccommit.html> | Atomic commit and crash/power-loss reasoning. |
| SQLite WAL | <https://www.sqlite.org/wal.html> | Reader/writer concurrency, WAL file handling, checkpointing. |
| SQLite foreign keys | <https://www.sqlite.org/foreignkeys.html> | Runtime enabling requirement. |
| SQLite backup | <https://www.sqlite.org/backup.html> | Consistent online backup snapshot. |
| SQLite FTS5 | <https://www.sqlite.org/fts5.html> | First local lexical-search index. |

### Audio preservation and privacy

| Topic | Source | Used for |
|---|---|---|
| Library of Congress sound preservation | <https://www.loc.gov/programs/national-recording-preservation-plan/sound-preservation/> | Broadcast WAV/96 kHz/24-bit preservation reference. |
| Library of Congress sound preferences | <https://www.loc.gov/preservation/digital/formats/content/sound_preferences.shtml> | PCM/BWF, higher sample rate/bit depth, metadata considerations. |
| IETF FLAC RFC | <https://www.rfc-editor.org/rfc/rfc9639.html> | Open, lossless, low-complexity derived audio option. |
| ICO consent | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/consent/how-should-we-obtain-record-and-manage-consent/> | Consent audit-trail fields and withdrawal. |
| ICO audio transparency | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/cctv-and-video-surveillance/guidance-on-video-surveillance-including-cctv/how-can-we-comply-with-the-data-protection-principles-when-using-surveillance-systems/> | Audio intrusiveness and clear recording signal. |
| ICO domestic purposes | <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/exemptions/a-guide-to-the-data-protection-exemptions/> | Legal-scope caveat; not treated as blanket permission. |

### Reusable projects and licenses

| Project | Source | License/status read 2026-09-12 |
|---|---|---|
| OpenAI Whisper | <https://github.com/openai/whisper> | MIT shown on repository. |
| whisper.cpp | <https://github.com/ggml-org/whisper.cpp> | MIT shown on repository. |
| SQLite source | <https://github.com/sqlite/sqlite/blob/master/LICENSE.md> | Core source public domain; some build scripts have separate terms. |
| Windows App SDK | <https://github.com/microsoft/WindowsAppSDK> | MIT shown on repository. |
| Mem0 | <https://github.com/mem0ai/mem0> | Apache-2.0 shown on repository. |
| Letta | <https://github.com/letta-ai/letta> | Apache-2.0 shown on repository. |
| FLAC | <https://github.com/xiph/flac> | Libraries BSD-like; tools/plugins have separate GPL/GFDL terms. |

## Evidence interpretation

Research sources establish capabilities and constraints; they do not prove MEMENTO’s target-language accuracy, safety, cost, or user fit. Those claims require later test evidence, recorded in milestone-specific reports and never inferred from documentation alone.
