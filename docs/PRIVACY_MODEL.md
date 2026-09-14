# MEMENTO privacy model

**Milestone:** M00.1 — Architecture and schema refinement
**Status:** Ready for architecture review
**Research baseline:** 2026-09-12

## Privacy objective

MEMENTO records living people. The default design is local-first, explicit, and minimising: record only when the participant chooses to record, preserve the family archive locally, send only the minimum content needed for the selected cloud capability, and make every transmission understandable to an administrator.

This document is an engineering design, not legal advice or a claim of regulatory compliance. Recording guests, using the product outside a private household, or sharing exports may create additional obligations. Obtain appropriate advice before expanding beyond the intended family use.

## Privacy modes

| Mode | Local audio | Cloud audio | Cloud text/context | Memory extraction | Use |
|---|---:|---:|---:|---:|---|
| `NORMAL` | Yes | Yes, only for the active permitted conversation path | Yes, minimum selected context | Candidate extraction allowed | Everyday interactive use. |
| `PRIVATE_CONVERSATION` | Yes | No automatic raw-audio upload | Typed or explicitly approved text only | Off by default | A private talk that should not be sent as audio or turned into memories automatically. |
| `LOCAL_CAPTURE_ONLY` | Yes | No | No | Queued locally only | Preserve an audio story for later, or operate offline. |

The UI must show the active mode and a persistent recording indicator. A participant can stop capture immediately. If `PRIVATE_CONVERSATION` is selected while a cloud voice session is active, the client ends that session before continuing locally.

## Data-flow inventory

| Data | Local archive | May leave device | Conditions |
|---|---:|---:|---|
| Original microphone audio / Source | Yes | Only in `NORMAL` and only for an enabled provider request | Never silently; retain local Source first. |
| Realtime input/output events | Yes, minimal operational copy | Yes in `NORMAL` | Do not rely on provider conversation state as the archive. |
| Durable transcription input | Yes | Yes when transcription is queued and permitted | Use local audio reference and retryable job state. |
| Raw and corrected transcripts / Evidence | Yes | Selected excerpts only | Corrections are append-only Evidence/Annotations; the original Source remains local. |
| Memory Claims | Yes | Only the selected, relevant claims and linked Evidence | A claim is never sent without its authority and temporal labels where relevant. |
| Response Episodes / derived patterns | Yes | Only selected episodes or derived results | Derived patterns must point to supporting episodes; hypothetical answers are never exported as observed episodes. |
| API key/client credential | Local vault only | Sent only in TLS authorization headers by the SDK/network stack | Never log, export, or put in source. |
| Current-information query | Local audit metadata | Yes when web search is enabled | Treat query and results as potentially sensitive and external. |
| Operational telemetry | Local, redacted | Optional and off by default for content | No raw audio, transcript, prompts, or secrets. |

## OpenAI data-control assumptions

The official OpenAI API data-controls documentation states that API data is not used to train or improve models unless the customer explicitly opts in. It also states that abuse-monitoring logs may contain customer content and are retained by default for up to 30 days, subject to stated exceptions. `/v1/responses` application state can be retained for at least 30 days when stored/default behaviour applies; a request can set `store=false`. Audio transcription is listed as having no application-state retention and as Zero Data Retention eligible in the table, while Realtime is listed with 30-day abuse-monitoring retention and as Zero Data Retention eligible. Eligibility, account settings, endpoint limitations, and policy requirements must be rechecked for the actual account before implementation.

MEMENTO therefore uses stateless/local persistence as the application design: do not depend on OpenAI-hosted conversations, set `store=false` where supported, do not upload family audio to Files or Vector Stores for permanent storage, and record the provider/model/retention assumptions in the admin evidence view. Zero Data Retention is an OpenAI organisational control requiring approval; MEMENTO must not claim to provide it itself.

## Consent

The primary participant must explicitly agree to recording, with a clear explanation in accessible language. The application records consent metadata: person/identifier, timestamp, privacy notice version, mode, scope, and withdrawal state. Consent is granular enough to distinguish local capture, cloud transcription, live cloud conversation, and family-admin sharing.

Guests and background speakers are not presumed to consent. The MVP should avoid multi-person recording or require a clear guest-consent flow before it is enabled. A visible indicator and optional spoken reminder are required; hidden or ambient recording is out of scope.

The ICO states that consent records should show who consented, when, what they were told, how consent was given, and whether it was withdrawn. Its guidance also stresses that audio recording is particularly intrusive and should be clearly signalled. MEMENTO adopts those as design requirements, regardless of whether a domestic-use exemption might apply to a particular deployment.

## Credentials and secret handling

The client never embeds an API key in source, a public configuration file, a log, a crash report, an export, or a prompt. On Windows, store credentials in Windows Credential Manager or protect a locally persisted secret with DPAPI. The application should minimise time in memory, use TLS, and redact headers and error bodies. The optional Realtime WebSocket adapter currently uses the same protected Credential Manager boundary; a short-lived client-secret or brokered-token flow must be selected and threat-modelled before continuous participant-facing Realtime use.

## Layered privacy and authority

Separating Source, Evidence, Memory Claim, Response Episode, and Annotation also separates privacy decisions. Raw audio Sources can contain substantially more sensitive information than a normalized Claim. A scoped export may include a Claim and its redacted Evidence while withholding the audio Source, but it must label the missing link rather than imply that the Source was exported. `ArchiveExporter.ExportRedacted` implements this boundary for an explicit set of reviewed Memory Claim IDs: it emits portable JSONL and a redaction manifest, while omitting raw SQLite, Source paths, transcript text, audio, and provider payloads. The Family Admin shell exposes the same reviewed-Claim selection path. Deletion and withdrawal workflows must identify affected Sources, transcript revisions, Evidence, Claims, Episodes, derived indexes, exports, and backups.

Authority is separate from privacy scope. A family administrator may be allowed to view or annotate a record without being allowed to make it `confirmed_by_speaker`. The export must preserve `speaker_confirmation`, `family_assessment`, `admin_annotation`, and any external assessment as separate attributed data.

## Local access and family roles

The Windows user account and disk encryption protect the local archive at rest. MEMENTO includes an optional application lock backed by the current Windows user's Credential Manager; it is disabled by default and must be configured and exercised during deployment. The current shell separates participant UI from Family Admin mode. Admin actions that accept, withdraw, export, restore, or delete Source, Evidence, Claim, Episode, or Annotation records are authenticated and written to an audit log without duplicating sensitive content unnecessarily. “Accept” is a workflow action on a candidate; it is not automatically speaker confirmation.

The archive is family-owned, not provider-owned. A public Git repository may contain documentation and schemas, never recordings, real transcripts, backups, exports, or credentials.

## Deletion and withdrawal

Deletion is a deliberate, authenticated workflow. It identifies the Source material, transcript revisions, Evidence, Memory Claims, Response Episodes, derived indexes, exports, and backups affected; records a minimal audit tombstone; and confirms that removal from all backup copies may require a separate backup-retention operation. Local deletion cannot guarantee physical erasure from flash storage, snapshots, or previously shared exports, so the UI must state its scope plainly.

Withdrawal from future AI use is distinct from deleting the historical record. A withdrawn record remains quarantined or tombstoned for audit purposes but is excluded from retrieval, extraction, and exports according to the chosen policy.

## Backups and exports

Backups are optional, encrypted, and family-controlled. The backup manifest lists archive version, database checksum, media checksums, source count, and creation time. Restore is tested on a separate destination before a backup is trusted.

Exports are explicit user actions and include only the selected scope. Export packages use ordinary JSONL, Markdown, and media formats. The conceptual record files are `sources.jsonl`, `evidence.jsonl`, `memory_claims.jsonl`, `response_episodes.jsonl`, and `annotations.jsonl`, plus the other person/transcript/index files needed by the selected scope. Each Claim retains links to its Evidence, and each Evidence retains links to permitted Source material. If an export is shared with another person or service, that is a new disclosure and must not happen automatically.

## Privacy-preserving observability

Operational logs contain event IDs, durations, status codes, retry counts, model IDs, and coarse usage data. They do not contain raw audio, full transcript text, API keys, full prompts, full search results, or extracted family details by default. A developer debug mode requires an explicit warning and time limit.

## Privacy review gates

Before a real-user pilot, verify:

- every recording state is visible and stoppable;
- each mode has an automated transmission test;
- credentials are absent from repository, logs, exports, and crash dumps;
- OpenAI retention settings are rechecked against current official documentation and account configuration;
- guest consent and withdrawal are tested;
- encrypted backup/restore and scoped export are tested;
- the privacy notice is understandable to the intended participant.

## Research sources

- OpenAI data controls: <https://developers.openai.com/api/docs/guides/your-data> — accessed 2026-09-12.
- OpenAI Responses `store` parameter: <https://developers.openai.com/api/reference/cli/resources/responses/methods/create> — accessed 2026-09-12.
- Microsoft secure credential handling: <https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords> — accessed 2026-09-12.
- ICO consent records: <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/consent/how-should-we-obtain-record-and-manage-consent/> — accessed 2026-09-12.
- ICO audio-recording transparency guidance: <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/cctv-and-video-surveillance/guidance-on-video-surveillance-including-cctv/how-can-we-comply-with-the-data-protection-principles-when-using-surveillance-systems/> — accessed 2026-09-12.
- ICO domestic-purpose guidance: <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/exemptions/a-guide-to-the-data-protection-exemptions/> — accessed 2026-09-12.
