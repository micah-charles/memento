# MEMENTO supervised pilot runbook

**Status:** Preparation documented and automated preflight verified; real-user M14 pilot has not started.

Do not run a real participant pilot until every item below has an owner and recorded evidence.

## Before the session

- Run the read-only deployment preflight: `.\scripts\Test-MementoPreflight.ps1`; resolve every blocking check and record the warnings before continuing.
- Confirm the participant and any guest understand local recording, optional cloud processing, retention, deletion, and who can review/export the archive.
- Confirm the microphone, Windows permissions, local disk capacity, backup destination, and restore procedure on the target machine.
- Run a short synthetic capture, stop it, inspect the Source checksum, and confirm the original audio remains playable.
- Decide the privacy mode before speaking. `LOCAL_CAPTURE_ONLY` must remain available if cloud consent is absent or withdrawn.
- Verify an authenticated Family Admin can review candidate claims without being able to impersonate speaker confirmation.

## During the session

- Keep the recording state and privacy mode visible.
- Stop immediately if the participant withdraws consent, the device disconnects, storage reports an error, or the participant becomes uncomfortable.
- Preserve the original audio and transcript revisions when clarification occurs; never replace a mistaken recognition silently.
- Treat weather, news, and other external answers as time-bound information, never as personal memory.

## After the session

- Confirm the session ended, the Source is finalized or explicitly recoverable, and any provider/job error is visible.
- Review a sample Source → transcript revision → Evidence → Claim chain and any clarification/vocabulary links.
- Export a small, redacted test package and restore it to a disposable directory.
- Record incidents, unresolved uncertainty, participant feedback, and rollback decision. Do not commit real recordings or transcripts.

## Stop and rollback

Stop the pilot if audio is lost, a cloud failure changes local evidence, a family/admin annotation appears as speaker confirmation, a secret is exposed, or the participant asks for deletion and the scope cannot be explained. Preserve only the minimum audit metadata needed to complete the agreed deletion/rollback process.
