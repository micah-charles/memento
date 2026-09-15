# Companion implementation evidence

**Checkpoint:** follow-on to `4f7bd13`; current code checkpoint `1c05d4a`. **Date:** 2026-09-15.

## Verified in this environment

- Release solution build completed with 0 warnings and 0 errors.
- Release test suite passed **230/230** after schema 19 and bridge fake-RPC coverage.
- PowerShell parser validation passed for all scripts; `dotnet format --verify-no-changes --no-restore` and `git diff --check` passed.
- The local Codex CLI 0.154.0 reused the existing ChatGPT account. The companion diagnostic completed three text turns on `gpt-5.6-luna` with a retained synthetic code word. No API key was used.
- `Setup-MementoLocalSpeech.ps1` downloaded and SHA-256 verified whisper.cpp v1.9.2 and `ggml-large-v3-turbo-q5_0.bin` outside Git.
- Windows local speech synthesis produced a Mandarin WAV using Microsoft Hanhan (`zh-TW`). Mandarin is owner-approved temporarily; no zh-HK voice is installed. Actual microphone recognition, playback and Cantonese quality remain unmeasured.
- The first fresh launch exposed a real XAML resource failure (`TabViewButtonBackground`) and exited with `0xC000027B`. The managed exception was captured in the local-only error log, the missing `XamlControlsResources` was added to App.xaml, and a fresh publish/install then passed process launch: title `MEMENTO`, responding `True`, graceful close exit code `0`.
- Fresh portable publish/install from `1c05d4a` created a payload manifest and installed it under `%LOCALAPPDATA%\MEMENTO\App`. Preflight verified the manifest source commit, **609/609** file hashes/lengths, Start Menu shortcut and per-user registration. SQLite integrity passed at schema version **19**.

## Explicitly not verified

Computer Use currently returns exactly `apps: []`, so no native Windows application window could be selected or visually inspected. This is an environment bridge limitation; process launch evidence above is not visual UI evidence. No microphone → Whisper → Codex → TTS three-turn test, playback interruption on hardware, family-admin interaction, offline retention drill, or real Cantonese measurement has passed.

The installed archive contains the user's existing data and was only migrated forward by starting and closing MEMENTO. No personal audio or transcript was transmitted by these checks. The synthetic diagnostic prompts are fixed and should not be used as a personal conversation test.

## Remaining acceptance gates

1. Complete fake-RPC/error and actual CLI tool-isolation verification; fail closed if a tool, MCP server or plugin can run in companion mode.
2. Test local Mandarin TTS playback and a real microphone loop for at least three turns, including stop and interrupt; record latency and intelligibility.
3. Use Computer Use or a user-supervised desktop session to visually confirm controls, setup, Family Admin timeline and audio playback.
4. Exercise backup/restore, source deletion/withdrawal and crash recovery with companion spans and derived audio.
5. Only after those checks call the spoken companion MVP complete.
