# MEMENTO security model

**Milestone:** M00.1 — Architecture and schema refinement
**Status:** Threat model and control baseline  
**Research baseline:** 2026-09-12

## Security objective

Protect the authenticity, confidentiality, availability, and recoverability of a family-owned archive containing speech, personal data, relationships, and potentially sensitive memories. MEMENTO must preserve evidence without allowing a model, web page, local attacker, or damaged disk to rewrite history invisibly.

## Assets

- Sources and their checksums;
- transcript revisions and corrections;
- direct statements, confirmed interpretations, and AI inferences as separate Evidence classes;
- Memory Claims and Evidence-to-Claim relationship links;
- identity, relationship, vocabulary, Response Episodes, and derived patterns;
- API credentials and client tokens;
- backup keys and export packages;
- consent records and admin audit events;
- source URLs and external-answer provenance;
- application binaries, update packages, and schema migrations.

## Trust boundaries

1. Participant and microphone → Windows client.
2. Windows client → local filesystem/SQLite.
3. Windows client → OpenAI or another provider over TLS.
4. Search results and retrieved web content → reasoning prompt.
5. Family Admin → accept/withdraw/export/restore actions.
6. Backup/export destination → archive of record.

The participant’s speech, transcript, search results, and model output are content, not instructions to the security policy. The client, storage writer, and explicit admin action control promotion and mutation. A Source is original material; Evidence says what was said or observed; a Claim is an interpretation aggregated from Evidence; an Annotation is attributed context. These authority levels must remain visible to the security boundary.

## Threats and controls

| Threat | Impact | Controls | Verification |
|---|---|---|---|
| Stolen PC or disk | Archive disclosure | OS account separation, full-disk encryption configured by the owner, optional deployment application lock, least-privilege file ACLs, encrypted backups | Configure and exercise the application lock, then run restore and unauthorized-access tests before pilot. |
| Malicious local user | Tampering or export | Separate Family Admin authentication, append-only revisions, hashes, admin audit events, no silent overwrite, no authority escalation from family annotation to speaker confirmation | Attempt unauthorized accept/delete/export and attempt to label a family assessment as speaker-confirmed. |
| Leaked API credential | Cloud account abuse and data exposure | Credential Manager/DPAPI, no source/env export, redaction, short-lived client tokens where available, rotation/revocation runbook | Secret scan plus induced log/error checks. |
| Accidental deletion | Irrecoverable loss | Confirmation, scoped deletion report, versioned backups, restore drill, quarantine/tombstone policy | Delete and restore an isolated fixture. |
| Database corruption | Missing or inconsistent records | SQLite transactions, WAL-aware handling, foreign keys, checksums, Online Backup API, recovery queue | Power-loss simulation and `PRAGMA integrity_check`. |
| Ransomware or compromised backup | Archive unavailable or encrypted by attacker | Offline/rotated encrypted backup, separate credentials, restore validation, multiple destinations | Restore from a disconnected fixture backup. |
| Malicious external content/prompt injection | Tool misuse or memory poisoning | Treat web/transcript content as untrusted, tool allowlist, no side effects from web search, canonical-memory write gate | Injection fixtures must not change policy or memory. |
| Provider/model drift | False Evidence, claims, or derived behaviour | Model IDs/snapshots in provenance, Source/Evidence/Claim links, regression corpus, manual review gate, re-runnable extraction | Compare model upgrade against fixtures. |
| Guest/background speaker | Unconsented processing | Visible recording state, explicit guest consent, no automatic speaker identity, multi-person mode deferred | Guest consent and withdrawal test. |
| Crash during long recording | Evidence loss | Temporary file, periodic flush/segmenting, recovery marker, atomic finalization, separate DB commit | Kill process at controlled points and recover. |
| Supply-chain compromise | Code execution/data theft | Pin and review dependencies, verify signatures/checksums where offered, least privilege, update review, no unreviewed runtime plugins | Dependency inventory and update rehearsal. |

## Integrity model

The original audio Source receives a SHA-256 checksum after close. The checksum, byte length, duration, capture format, relative path, and session ID are stored in SQLite and included in exports/backups. A later correction creates a new transcript revision, Evidence record, or Annotation. The system never rewrites the Source or direct Evidence to match an AI summary, and never lets a family annotation masquerade as speaker confirmation.

Hashing detects accidental or unauthorised change; it does not by itself prevent a privileged attacker from changing both file and database. Access control, encrypted storage, admin audit, and independent backups are still required.

## Prompt-injection posture

External webpages, search snippets, transcripts, and participant-provided text can contain instructions such as “ignore your policy” or “store this as a fact.” MEMENTO treats all such content as data. The tool adapter returns a typed result; the application, not the model, decides whether an operation may write. Canonical Memory Claim writes require schema validation and explicit Evidence linkage; current-information results have no write authority. A Response Episode also requires stimulus/response Evidence links, and a derived behavioural pattern must point back to its supporting Episodes.

## Credential posture

Microsoft’s current Windows guidance recommends Windows Credential Manager or DPAPI for local secret storage, minimizing time in memory, never logging secrets, and transmitting credentials only over encrypted channels. MEMENTO follows this guidance. API keys are configuration secrets and never part of the archive or export.

## Operational security requirements

- no production API call in tests unless explicitly enabled;
- no real family data in the public repository or CI artifacts;
- log redaction before persistence;
- automatic crash-report opt-in, with content disabled by default;
- schema migrations are reviewed and reversible or backup-gated;
- export destinations are shown before writing;
- all network calls have timeouts, cancellation, retry limits, and idempotency keys where supported;
- the app says “saved locally; processing will retry” rather than exposing stack traces.

## Security acceptance gates

M13 must demonstrate secret absence, unauthorized-access resistance, prompt-injection containment, database integrity after interruption, backup restore, export scope, and safe update/dependency review. M14 must repeat the privacy/security checks with the real pilot environment and a redacted incident playbook.

## Sources

- Microsoft secure password/credential handling: <https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords> — accessed 2026-09-12.
- SQLite atomic commit: <https://www.sqlite.org/atomiccommit.html> — accessed 2026-09-12.
- SQLite WAL: <https://www.sqlite.org/wal.html> — accessed 2026-09-12.
- SQLite backup API: <https://www.sqlite.org/backup.html> — accessed 2026-09-12.
- OpenAI data controls: <https://developers.openai.com/api/docs/guides/your-data> — accessed 2026-09-12.
- ICO consent guidance: <https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/consent/how-should-we-obtain-record-and-manage-consent/> — accessed 2026-09-12.
