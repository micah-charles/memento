# MEMENTO

MEMENTO is a proposed Windows-first, Cantonese-friendly AI companion whose most important output is a trustworthy, family-owned archive of a living person’s memories and conversational behaviour.

## Current status: M13 foundations implemented (live and supervised gates pending)

M00.1 architecture is approved for implementation. The repository now contains a self-contained WinUI 3 shell, local SQLite archive, crash-safe PCM capture, provider boundaries, clarification and provenance layers, export/backup, and health checks. Live provider credentials, physical microphone/native-window observation, OS-backed admin authentication, and real-user pilot review remain explicit gates; see [PROGRESS](docs/PROGRESS.md).

The governing principle is:

> Evidence first. Interpretation second. Simulation last.

## Review order

1. [Product vision](docs/PRODUCT_VISION.md)
2. [Architecture](docs/ARCHITECTURE.md)
3. [Memory and provenance model](docs/MEMORY_MODEL.md)
4. [Privacy model](docs/PRIVACY_MODEL.md)
5. [Technology decisions](docs/TECHNOLOGY_DECISIONS.md)
6. [OpenAI research](docs/OPENAI_RESEARCH.md)
7. [Security model](docs/SECURITY_MODEL.md)
8. [Cost model](docs/COST_MODEL.md)
9. [Roadmap and future acceptance tests](docs/ROADMAP.md)
10. [ADRs](docs/adr/)

Project state is kept in [PROGRESS](docs/PROGRESS.md), [DECISIONS](docs/DECISIONS.md), [KNOWN_ISSUES](docs/KNOWN_ISSUES.md), and [EVIDENCE](docs/EVIDENCE.md).

## Start the Windows app

From a Windows machine with the .NET 10 SDK and Windows App SDK build prerequisites installed:

```powershell
dotnet build .\src\Memento.App\Memento.App.csproj --configuration Release
Start-Process .\src\Memento.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Memento.App.exe
```

The app is currently an unpackaged self-contained executable. It stores local data under `%LOCALAPPDATA%\MEMENTO`; an installer/MSIX package and Start Menu registration are still deployment work, not required for the current development build.

To create a portable self-contained bundle:

```powershell
.\scripts\Publish-Memento.ps1
```

## Non-goals for M00/M00.1

M00/M00.1 did not build a production application, start WinUI or SQLite runtime code, call an AI API, capture real audio, fine-tune a model, create a voice clone or avatar, require a local LLM/GPU, or create a cloud-hosted permanent family-memory database. Later milestones now implement local capture and archive foundations while live integrations remain gated.

## Repository shape

```text
.
├── docs/       Product, architecture, research, operating models, and ADRs
├── schemas/    Versioned documentation-level data contracts
├── src/        Reserved for later Windows implementation
├── tests/      Reserved for later automated and manual verification
├── scripts/    Reserved for later maintenance and validation tooling
└── samples/    Reserved for consented, synthetic, or redacted test fixtures
```

## Research currency

Current API, pricing, platform, privacy, and license claims in the documents are dated **2026-09-12** and include source URLs. Re-check those sources at the start of any implementation milestone; model aliases, pricing, endpoint behaviour, Windows SDK versions, and legal guidance can change.

## Git and public-repository hygiene

The intended public remote is `https://github.com/micah-charles/memento`. Real recordings, transcripts, exports, backups, logs, credentials, and API responses must never be committed. The initial `.gitignore` excludes those paths. No credentials are stored in this repository.
