# ADR-002: Local storage and archival media

**Status:** Proposed — awaiting M00 architecture review  
**Date:** 2026-09-12

## Context

MEMENTO’s main asset is a multi-decade family archive. Audio must survive provider/model changes, network failure, and application replacement. The system needs transactional metadata, searchable transcripts, reliable recovery, ordinary export formats, and backups that do not corrupt a live database.

## Options considered

1. SQLite metadata plus filesystem media.
2. A cloud-hosted database and object store.
3. A document database with embedded media.
4. Plain files only.

## Decision

Use SQLite for structured metadata and filesystem files for audio, photos, and videos. Use SQLite WAL mode, explicit transactions, foreign keys enabled on every connection, FTS5 for first lexical search, and the Online Backup API or `VACUUM INTO` for consistent snapshots. Store relative media paths, checksums, format, duration, and provenance in SQLite; do not embed large media blobs by default.

The archival master is lossless PCM in WAV/BWF-style packaging where practical. Capture the stable device-supported PCM format, preferably mono 48 kHz/24-bit for the speech MVP; a derived FLAC copy may reduce storage but never replaces the original evidence asset.

## Reasoning

SQLite provides atomic transactions and a portable file format; WAL permits readers and writers to proceed concurrently on one host but introduces a `-wal` file that must be included in safe handling. FTS5 is enough for an initial local search layer. The Library of Congress treats Broadcast WAV/PCM and higher-resolution audio as preservation references, while FLAC is an open lossless standard suitable as a derived copy.

## Consequences

Positive: family-owned, portable, exportable, low operational overhead, and resilient to provider replacement.  
Negative: the app must coordinate database/media backups, manage WAL/checkpoints, protect local files, and build its own migrations/search policy.

## Revisit conditions

Revisit if archive size, concurrent multi-device access, backup requirements, or search scale exceeds SQLite’s practical single-host model. Any new store must preserve the same export and provenance contract.

## Sources

- <https://www.sqlite.org/atomiccommit.html> — accessed 2026-09-12.
- <https://www.sqlite.org/wal.html> — accessed 2026-09-12.
- <https://www.sqlite.org/backup.html> — accessed 2026-09-12.
- <https://www.sqlite.org/fts5.html> — accessed 2026-09-12.
- <https://www.loc.gov/preservation/digital/formats/content/sound_preferences.shtml> — accessed 2026-09-12.
- <https://www.rfc-editor.org/rfc/rfc9639.html> — accessed 2026-09-12.
