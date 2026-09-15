# MEMENTO companion architecture — implementation contract

Approved: 2026-09-15. Baseline: `3526144`; Windows x64, .NET 10/WinUI, Codex CLI 0.154.0.

## Product and account policy

MEMENTO is an accessible Cantonese voice interface to a real AI conversation, with a local family-owned archive. It is not merely a recorder. One start action opens a multi-turn conversation; the AI greets, listens, answers aloud, and listens again. Family settings, archive review, correction, and maintenance live behind the participant surface.

Reuse the existing ChatGPT account through local Codex. The local process still sends conversation text to the cloud. Additional billed APIs are opt-in, never an automatic fallback. Prefer local STT/TTS when ChatGPT's native voice is not exposed. Original voice is the primary evidence; future voice research/training is an archival purpose, not a current cloning feature.

## Architecture

Participant UI → ConversationCoordinator → continuous local capture / utterance detector → local STT → Codex App Server → local TTS/playback. All stages write provenance to the local archive.

- Codex uses stdio JSON-RPC, managed login, runtime model discovery, one backend thread per local session, and cancellation. No copied tokens, browser scraping, cookies, or UI automation bridge.
- Prefer `gpt-5.6-luna` with low reasoning if advertised; otherwise require family selection. Do not silently upgrade the model or infer prices from names.
- Isolate the companion from development work: empty working directory, disabled tools/MCP/plugins and constrained permissions. Verify enforcement; a prompt alone is insufficient.
- Preserve raw streamed answers and exact sent text. First version speaks the completed response once. Companion instructions request brief Cantonese, at most one follow-up question, tolerance for silence, and explicit uncertainty.
- Local transcription uses pinned whisper.cpp and multilingual `large-v3-turbo-q5_0` as the initial measured candidate. Assets and checksums live outside Git. Cantonese accuracy and CPU latency need physical-machine measurement.
- Local synthesis uses Windows SpeechSynthesizer and actual enumerated voices, preferring zh-HK. **Owner update, 2026-09-15:** Mandarin is explicitly accepted temporarily. When zh-HK is unavailable, select an installed zh-CN/zh-TW voice and disclose the selection in setup; missing Cantonese TTS no longer blocks local trials.
- Existing API adapters remain optional. Automatic memory extraction stays off until the conversation loop is stable.

Interfaces: ICompanionBackend (probe, begin/resume, send, cancel), ILocalTranscriptionProvider (source ranges to timestamped transcript), ISpeechOutputProvider (existing contract), IUtteranceDetector, and ConversationCoordinator (single lifecycle owner). Text requests do not require a fake audio file.

## Lifecycle and evidence

Idle → Greeting → Listening → Transcribing → Thinking → Speaking → Listening. Every active state can enter RecoverableError or Ending/Ended. One response pipeline, session cancellation, no late playback. Initial end-of-utterance silence is 1.8 s, configurable by family. During playback, microphone audio remains archived but does not trigger another response. An explicit “我想講” action cancels playback and listens again. Automatic acoustic barge-in is deferred.

Capture original microphone PCM continuously into 60-second segments with contiguous sample offsets. Map turns to source spans, including turns spanning files. Preserve pauses and laughter. Mark playback intervals and possible speaker overlap; VAD is not speaker identification. Generated speech is separately labelled derived audio, never participant evidence.

Keep immutable initial recognition, append-only corrected/edited versions with parent/author/reason/time, exact revision sent to AI, raw AI answer, separately recorded spoken version if transformed, generated audio identity, playback intervals, and cancelled/partial states. Reuse Sources, Turns, TranscriptRevisions and DerivedAudioStore. New span validation must not weaken legacy Source/Turn validation. Use forward migrations, never recreate the archive.

Network/auth/quota failure preserves recording and offers recovery without switching to a paid API. Ambiguous delivery is not automatically retried. Stop/revocation cancel work; stale completions cannot play. Restart never automatically opens the microphone. Provider-side data already sent cannot be claimed withdrawn merely by local deletion. Local-only blocks all remote backends, including Codex. Add independent CloudConversation consent.

## Checkpoints

A. Document and implement bridge; probe capabilities/auth/model/tool restrictions and prove three text turns retain context.
B. Local STT/TTS, voice selector, capture and real Cantonese measurements.
C. Coordinator, automatic multi-turn interaction, interruption and stop.
D. Continuous source spans, revision/timeline review, exact inputs and playback evidence.
E. Publish from committed code, update local installation, verify all payload files and build commit, then supervised voice acceptance.

Commit passing checkpoints with evidence. No personal recordings/transcripts/credentials/models in Git. Do not change the user's selected development model. The earlier overnight completion did not establish companion MVP completion.

## Acceptance

Automate bridge protocol/errors/cancellation, three-turn context and session isolation, lossless segment boundaries, recovery, immutable revisions, once-only playback, late-result suppression, privacy/revocation races, migrations/backup/restore, and existing regression gates.

Real acceptance: Start Menu opens the verified new build; one click starts greeting; three spoken Cantonese turns require no manual recording/transcription/play buttons; later turns remember earlier context; interrupt/end work; family review links both text versions and AI replies to original voice; disconnect retains audio; no extra API key is required. Record real latency and intelligibility. Mocks, process handles and synthetic tests cannot replace this evidence.

Deployment verification includes build commit, Memento.App.dll, Memento.Core.dll, resources, and a complete manifest, not just the apphost EXE. At baseline the installed App DLL differed from the Release build: previous EXE-to-old-bundle matching did not prove the latest build was installed.

## Sources

- https://learn.chatgpt.com/docs/app-server
- https://learn.chatgpt.com/docs/auth
- https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md
- https://learn.microsoft.com/en-us/uwp/api/windows.media.speechsynthesis.speechsynthesizer.allvoices

## Verification status

Architecture approved; implementation checkpoints must report IMPLEMENTED, AUTOMATED VERIFIED, or LIVE VERIFIED independently. Full voice MVP remains pending end-to-end acceptance; temporary Mandarin speech is now owner-approved. See [Luna handoff](LUNA_HANDOFF.md) for implemented components, verification evidence, known gaps and implementation differences.
