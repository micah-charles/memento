# MEMENTO known issues and open questions

**Last updated:** 2026-09-12

These are deliberately visible. They are not reasons to invent a confident answer.

| ID | Issue/open question | Impact | Owner/gate |
|---|---|---|---|
| K-001 | The reviewed OpenAI sources do not guarantee Hong Kong Cantonese or mixed Cantonese/Mandarin/English accuracy. | High: core user experience and entity provenance. | Measure in M04 before pilot claims. |
| K-002 | Realtime audio prices are token-based; a universal minute conversion is not documented in the reviewed source. | High: operating-cost uncertainty. | Measure representative usage in M03 and update COST_MODEL. |
| K-003 | Exact Realtime transport and safe client credential flow on Windows are not selected. | High: privacy/security and deployment. | Prototype and threat-model in M03. |
| K-004 | Shared Windows audio mix formats vary by device and configuration. | Medium: archive consistency. | Capture real device metadata in M02; preserve original PCM before conversion. |
| K-005 | Long-term backup destination, encryption-key recovery, and family succession are not designed in detail. | High: decades-long availability. | Design/test in M12–M14. |
| K-006 | Guest consent and multi-speaker attribution are deferred. | High if scope expands beyond one participant. | Keep disabled until a consent and diarization design passes review. |
| K-007 | “Corrected transcript” semantics need UX testing so corrections never appear to rewrite direct speech. | Medium: epistemic clarity. | Prototype in M05 and M11. |
| K-008 | SQLite FTS5 language/tokenization behaviour for Chinese text needs real-corpus evaluation. | Medium: retrieval quality. | Evaluate in M06–M09 before semantic search. |
| K-009 | OpenAI pricing, model aliases, endpoint retention, and platform docs may change. | Medium: stale planning claims. | Re-check before every milestone. |
| K-010 | Legal status varies by jurisdiction, purpose, household/commercial context, and guest situation. | High: consent/disclosure risk. | Obtain appropriate advice before pilot or sharing. |
| K-011 | Native WinUI visual/manual verification is unavailable in the current execution surface. | High: M01 gate cannot be honestly passed from this environment alone. | Run M01 on target Windows hardware with native GUI observation. |

## M00 limitations

No network-backed API call, Windows audio capture, crash simulation, real language corpus, or account-specific retention configuration was tested in M00. The documents record the source-backed design and label assumptions so those gaps can be addressed in later gates.
