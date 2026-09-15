# MEMENTO companion checkpoint — continue with Luna

Date: 2026-09-15. Starting commit: `3526144`. This document ships in the checkpoint commit; use `git log -1` for its hash.

## Owner's latest instruction

Stop at a reviewable checkpoint to save tokens; the owner will select Luna and continue this same task. **Mandarin speech is explicitly acceptable temporarily. Missing zh-HK TTS is not a blocker to continuing implementation or local testing.** Do not change the owner's development model automatically. No additional paid API fallback is authorized.

Read [the companion architecture](MEMENTO_COMPANION_ARCHITECTURE.md) first. It supersedes the old API-first/recorder interaction priority. This is a partial implementation, approximately 60% by engineering estimate, not a measured acceptance percentage and not an MVP completion claim.

## What is implemented

- `src/Memento.Core/Companion`: owned Codex stdio JSON-RPC process, ChatGPT login check, paginated model discovery, explicit model/low reasoning selection, one thread per session, streamed text, interruption and request IDs. No login token is copied or read by MEMENTO.
- `ConversationCoordinator`: greeting, listening, transcription, thinking, speech, automatic next listening turn, one pipeline, cancellation, privacy checks and local archival. A late-result-after-End regression was reproduced and fixed by checking the synchronous ending flag before accepting results.
- Continuous 48 kHz mono PCM16 capture, maximum 60-second files, earlier finalization at utterance boundaries, absolute sample offsets and cross-file spans. Capture continues while AI speaks; endpointing only runs while Listening. Energy endpointing defaults to 1.8 seconds; manual interruption is supported.
- Local whisper.cpp process adapter and Windows `SpeechSynthesizer` adapter. Whisper setup script downloads pinned runtime/model and checks SHA-256 before installing into local data, outside Git.
- Schema 19 introduces independent `CloudConversation` consent plus companion sessions, messages, text versions, timestamped recognition segments, chunks, spans and playback intervals. Family review can view revisions, append a correction, and play original spans.
- Legacy API adapters remain in source but default app startup does not activate them unless the separate `optional_paid_api_enabled` setting is `1`. Automatic memory extraction remains off.
- Participant start/stop/status and family setup are wired to the coordinator. Voice selection honors a saved voice, then prefers zh-HK, zh-CN and zh-TW. Temporary Mandarin selection is disclosed in setup, following the owner's explicit approval.

## Evidence actually obtained

- Real Codex CLI 0.154.0, existing ChatGPT account: three text turns on `gpt-5.6-luna` retained the synthetic code word `紫色茶杯`. No API key was used. This proves the text bridge/context path, not the voice UI.
- Windows voice enumeration returned David/Zira/Mark (en-US) and Hanhan/Yating/Zhiwei (zh-TW). No zh-HK voice was available. Mandarin is now accepted for temporary testing.
- `Setup-MementoLocalSpeech.ps1` completed. Local assets are under `%LOCALAPPDATA%\MEMENTO\speech`; `local-speech.json` contains the executable/model locations.
- Pinned runtime: whisper.cpp v1.9.2, archive SHA-256 `49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a`.
- Pinned model: `ggml-large-v3-turbo-q5_0.bin`, SHA-256 `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` (574041195 bytes). Actual recognition accuracy/latency has not yet been measured.
- After the stopping-race correction, Core tests passed **226/226** in Debug and **226/226** in Release. Release solution build passed with **0 warnings, 0 errors**; `dotnet format --verify-no-changes --no-restore`, `git diff --check` and the new PowerShell script parser check all passed.
- No real microphone, spoken three-turn loop, playback interruption on hardware, or native WinUI visual acceptance was performed in this checkpoint. Prior Computer Use exposed `apps: []`; do not replace visual acceptance with process/window-handle inspection.
- No fresh publish/install was performed in this checkpoint. The existing installed app must not be assumed to contain this code; its App DLL previously differed from the Release build.
- A local TTS diagnostic now synthesized a Mandarin WAV with Microsoft Hanhan (`zh-TW`) successfully. Playback and microphone/STT loop still need supervised testing.

## Next work, in priority order

1. **Bridge isolation and protocol verification.** Read `CodexRpc.cs` and `CodexCompanionBackend.cs`. Current controls disable feature flags, configured MCP/plugin entries, web and update-plan, reject server tool requests, and require read-only sandbox/model acknowledgement. Complete enforcement verification against this installed CLI. Read-only alone does not prevent file reads, and prompt instructions are not tool isolation. If tools cannot actually be disabled, fail closed and report a pending bridge gate before real personal conversations. Check plugin/MCP key quoting and inherited hooks/config. Add fake-RPC tests for initialization, missing login, unsupported model/protocol, stream/final events, cancellation, timeout, process exit and stale notifications. Existing coordinator tests use a fake high-level backend, not protocol fakes.
2. **Harden archive and lifecycle.** Message/turn/initial-revision writes are now atomic and Whisper segment timestamps are persisted in schema 19. Verify Source+chunk registration atomicity and crash recovery of the active segment's absolute sample offset; the legacy recovery service preserves WAV bytes but does not yet reconstruct all companion chunk metadata. Add migration-from-17, backup/restore, source deletion/derived-audio cleanup and playback provenance tests. Test exactly-once playback, stop/interrupt/revoke races, startup cancellation, repeated disposal and shutdown callbacks. Confirm partial streamed text on non-cancellation errors and inputRevision/request/turn IDs survive status-only updates (COALESCE fix is present).
3. **Measure local speech and complete interaction.** Use the installed Mandarin voice temporarily, with an actual local synthesis/playback test and then microphone → Whisper → Codex → TTS for at least three turns. First synthetic checks are useful but must be labelled synthetic. Do not call Cantonese quality verified. Audit capture stop/final-buffer handling, silence thresholds, no-speech/max-duration behavior and speaker feedback. Keep uninterrupted original audio; AI playback overlap is not clean participant training material. Review family authorization, setup placement, active controls and error recovery on real UI.
4. **Fresh deployment identity.** Publish now checks the `dotnet publish` exit code and writes a complete payload manifest containing source commit, App/Core DLLs, UI `.pri` resources and hashes; preflight hashes the ZIP once and compares every manifest file with the installed tree. Finish the install/update and Start Menu smoke test. Before any further recursive filesystem delete/move, verify resolved targets stay inside the intended publish/install directory. Current installed app is known stale (old DLL and schema 17).
5. **Acceptance and documentation.** Record actual hardware/voice/latency and three-turn context, interrupt, end, archive review/revisions and offline raw retention. Update progress/evidence honestly. No unrelated backend milestone or automatic memory extraction work until this loop is stable. Only declare companion MVP complete after the agreed end-to-end checks pass (Mandarin may temporarily cover local trial speech per the owner).

## Intentional implementation differences to review

- New companion text uses `companion_text_versions`, rather than loosening legacy single-Source `TranscriptRevisions` constraints. It reuses legacy Sessions, Turns, Sources and DerivedAudioStore. Multi-file spans are validated separately. Existing transcript tables remain intact.
- Segments are **at most** 60 seconds: an utterance boundary may finalize one early so its Source exists before a turn attaches spans. Raw samples are retained across these boundaries; the sample reconstruction test passes.
- Spoken text is presently identical to the original assistant answer, so playback references that revision. If text cleanup/translation is introduced for Mandarin, preserve a separate spoken revision instead of overwriting the original.
- Source withdrawal blocks that session's subsequent context; source deletion conservatively removes companion messages for the entire dependent session. Review privacy/export behavior without weakening legacy provenance.

## Useful local commands

Run from `C:\AI\memento`:

```powershell
dotnet test Memento.slnx --configuration Release
dotnet build Memento.slnx --configuration Release --no-restore
dotnet format Memento.slnx --verify-no-changes --no-restore
dotnet run --project tools/Memento.CompanionCheck -- --voices
dotnet run --project tools/Memento.CompanionCheck
# Uses account quota for three synthetic conversation turns:
dotnet run --project tools/Memento.CompanionCheck -- --live
# Process-only execution policy override, if setup needs rerunning:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup-MementoLocalSpeech.ps1
```

Do not commit local configuration, generated artifacts, model binaries, credentials or personal audio/conversations. The diagnostic tool's default probe reads capabilities; `--live` sends only its fixed synthetic test prompts. Keep working in the same repository; do not reimplement completed parts or silently substitute paid APIs.

## Suggested continuation prompt

> Read docs/LUNA_HANDOFF.md and docs/MEMENTO_COMPANION_ARCHITECTURE.md, inspect the checkpoint and continue the outstanding implementation in order. Mandarin TTS is explicitly allowed temporarily. Preserve original audio, exact text revisions and privacy guarantees. Finish isolation/lifecycle/storage checks before personal voice testing, then publish and verify the latest installed payload. Do not claim full MVP completion from mocks or a text bridge alone. Commit verified checkpoints and record remaining real-user acceptance honestly.
