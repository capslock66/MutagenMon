# Findings & Decisions

## Requirements
- A developer clears the alpha folder and refills it immediately; Mutagen's scan catches the empty instant and halts the session with "Halted due to one-sided root emptying". This is expected and must be resolved without manual work.
- mutagenMon must detect the halt and (optionally, per session via a flag) resolve it automatically.
- Resolution only for two-way-safe sessions, only when the beta path is not a root.

## Research Findings
- Today mutagenMon does not handle `Halted…` at all: no reference in the repository. `SessionStateTracker.Update` leaves the code unchanged for an unrecognised status (it keeps the previous code).
- Consequence confirmed by the user's screenshot: the halted session keeps a green icon (Ready) while the text shows the halt. A session already halted when the app starts stays `Unknown` (lightgray-init, "waiting for status...").
- `RestartUnhealthySessionsAsync` only covers: session absent, duplicate, "Connecting…". A halted session is never restarted, so there is no destructive loop.
- Real status text: "Halted due to one-sided root emptying". Other Mutagen halts (root deletion, root type change) are invisible in the same way.
- Mutagen docs (https://mutagen.io/documentation/synchronization/safety-mechanisms):
  - Trigger: both endpoints are directories and one endpoint (but not both) deletes a non-trivial amount of content and leaves the root empty; detection is best-effort.
  - Recovery: "the session can be resumed by manually deleting the content that would be deleted or replaced and then invoking `mutagen sync resume`"; history can be reset with `mutagen sync reset`, which makes Mutagen see the undeleted content as new content and propagate it back.
  - Not stated: which sync modes apply, the both-empty case, any option to disable or tune the check.
- Behaviour deduced (NOT executed, to be tested): after purge + reset the ancestor is empty, so alpha's content is seen as new and propagated to beta, with no deletion left to detect.
- "Beta wins" is mutagenMon's own conflict-dialog default (FR-9.2), not a property of two-way-safe. In Mutagen two-way-safe has no winner; two-way-resolved lets alpha win.
- Beta path in the session, e.g. `IA-VM:sources/tmp`, is relative to the SSH home.
- Existing helpers to reuse: `MutagenMon.Core/Ssh/` (SshConfigParser, ShellQuoting, SftpDirectoryBrowser), `ScriptRunner`, `MutagenCliClient`, `SessionCommandLineParser` (reads the sync mode).

## Technical Decisions
| Decision | Rationale |
|----------|-----------|
| See task_plan.md "Decisions Made" | Single source for decisions |

## Issues Encountered
| Issue | Resolution |
|-------|------------|
| Open: does `mutagen sync reset` leave the session paused? | Test on a throwaway session; keep `resume` in the sequence regardless |
| Open: flag name and location (per-session key vs global default) | Decide in Phase 2, document in requirements/06-configuration-reference.md |
| Open: alpha stability delay (wait N seconds of unchanged content before acting) | Decide in Phase 2 |
| Open: cooldown between two automatic attempts on the same session | Decide in Phase 2 |
| Residual risk: alpha non-empty but only partially refilled when reset runs | Accepted: later content propagates normally; optional stability delay |
| Residual risk: a mount point that is empty looks like a cleared folder | Guard "alpha not empty" blocks the purge; limit the flag to ordinary local folders |
| Side effect: purge deletes beta files Mutagen ignores and beta-only unsynced files | Accepted for mirror sessions; document in the confirmation dialog |
| Cost: each automatic run wipes beta and re-uploads everything | Acceptable for small trees; add a rate limit |
| Alternative, not chosen: one-way-replica for pure mirrors | Docs do not say whether the check applies to that mode; changes current two-way-safe behaviour |

## Resources
- https://mutagen.io/documentation/synchronization/safety-mechanisms
- requirements/03-tray-icon-requirements.md §3 and §7
- requirements/06-configuration-reference.md
- MutagenMon.Core/Status/SessionStateTracker.cs
- MutagenMon.Core/Monitoring/SessionMonitorService.cs
