# ADR-001: Windows application framework

**Status:** Proposed — awaiting M00 architecture review  
**Date:** 2026-09-12

## Context

The first user is an older family member on a modest Windows PC. MEMENTO needs a simple accessible UI, reliable local filesystem/database access, microphone integration, clear recording state, and maintainable deployment. The product does not need a browser runtime or cross-platform UI in the first milestone.

## Options considered

1. WinUI 3 with Windows App SDK, C#/.NET and XAML.
2. WPF with .NET.
3. Tauri with a web frontend and native plugins.
4. Electron.

## Decision

Use C#/.NET with WinUI 3 on the Windows App SDK for the first implementation. Keep application/domain contracts independent of the UI framework so WPF or another surface remains possible if evidence justifies a change.

## Reasoning

Microsoft currently identifies WinUI 3 and the Windows App SDK as the recommended path for new native Windows desktop applications. WinUI provides XAML, modern controls, native Windows integration, and C#/C++ support. This fits the target device and avoids spending the first milestone on browser-runtime packaging or cross-platform abstractions.

WPF remains the fallback if target accessibility, deployment, or control-library testing shows a concrete advantage. Tauri and Electron remain future portability options, not the starting point.

## Consequences

Positive: native Windows UX, direct platform APIs, clear separation between participant and admin views, and no mandatory embedded browser runtime.  
Negative: Windows-first codebase, Windows build tooling, and a future portability cost. WinUI SDK versioning and packaging must be maintained.

## Revisit conditions

Revisit if the target PC cannot run the selected Windows App SDK baseline, accessibility testing fails, packaging becomes unreliable, or a second platform becomes a committed product requirement.

## Sources

- <https://learn.microsoft.com/en-us/windows/apps/> — accessed 2026-09-12.
- <https://learn.microsoft.com/en-us/windows/apps/winui/winui3/> — accessed 2026-09-12.
