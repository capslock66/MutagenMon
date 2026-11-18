# Findings & Decisions

## Requirements
- A developer clears a synchronised folder and refills it immediately; Mutagen's scan catches the empty instant and halts the session with "Halted due to one-sided root emptying". This is expected and must be resolved without manual work.
- mutagenMon must detect the halt and (optionally, per session via a flag) resolve it automatically.
- Resolution only for two-way-safe sessions, only when the purged side's path is not a filesystem root.
- Either side (alpha or beta) can be the emptied one, and either side can be a local folder or an SSH server.

## Research Findings
- Today mutagenMon does not handle `Halted…` at all: no reference in the repository. `SessionStateTracker.Update` leaves the code unchanged for an unrecognised status (it keeps the previous code).
- Consequence confirmed by the user's screenshot: the halted session keeps a green icon (Ready) while the text shows the halt. A session already halted when the app starts stays `Unknown` (lightgray-init, "waiting for status...").
- `RestartUnhealthySessionsAsync` only covers: session absent, duplicate, "Connecting…". A halted session is never restarted, so there is no destructive loop.
- Real status text: "Halted due to one-sided root emptying". Other Mutagen halts (root deletion, root type change) are invisible in the same way.
- User test 2026-11-18 (alpha files deleted on `temp`), output of `mutagen sync list temp` (no `-l`):
  ```
  Name: temp
  Identifier: sync_fLCXdLtWSvX0fLJ6ZEnISzInISlm3SNKm36lFBgutWb
  Alpha:
          URL: C:\sources\temp
          Connected: Yes
          Synchronizable contents:
                  1 directory
                  0 files (0 B)
                  0 symbolic links
  Beta:
          URL: robbie:sources/temp
          Connected: Yes
          Synchronizable contents:
                  1 directory
                  4 files (0 B)
                  0 symbolic links
  Status: Halted due to one-sided root emptying
  ```
  - The "Synchronizable contents" counts stay identical while the session is halted, whatever is added or changed on either side: they are the snapshot taken when the halt occurred. This matches the Mutagen source (the halted run loop is parked and does not rescan).
  - The emptied side is the one reduced to its root directory (1 directory, 0 files, 0 symbolic links) while the other side has content.
  - Inverse test by the user: emptying beta gives the mirror output, so the side to purge is then alpha.
  - Deleting a single file does not halt Mutagen (the docs say "a non-trivial amount of content"; the threshold is unknown, 4 files were enough).
- Parser gap: `SessionMonitorService` reads Name, Identifier, Status, URL, Staging, Conflicts, Problems and the conflict lines; it does not read "Synchronizable contents". Only the singular "1 directory" is seen in the sample; the plural wording ("N directories") and the unit suffix on files ("0 B") must be handled tolerantly (match the number followed by a keyword prefix: `director`, `file`, `symbolic link`).
- Mutagen docs (https://mutagen.io/documentation/synchronization/safety-mechanisms):
  - Trigger: both endpoints are directories and one endpoint (but not both) deletes a non-trivial amount of content and leaves the root empty; detection is best-effort.
  - Recovery: "the session can be resumed by manually deleting the content that would be deleted or replaced and then invoking `mutagen sync resume`"; history can be reset with `mutagen sync reset`, which makes Mutagen see the undeleted content as new content and propagate it back.
  - Not stated: which sync modes apply, the both-empty case, any option to disable or tune the check.
- Behaviour deduced (NOT executed, to be tested): after purge + reset the ancestor is empty, so the emptied side's content is seen as new and propagated to the purged side, with no deletion left to detect.
- "Beta wins" is mutagenMon's own conflict-dialog default (FR-9.2), not a property of two-way-safe. In Mutagen two-way-safe has no winner; two-way-resolved lets alpha win.
- An SSH endpoint path in a session, e.g. `robbie:sources/temp`, is relative to the SSH home.
- Existing helpers to reuse: `MutagenMon.Core/Ssh/` (SshConfigParser, ShellQuoting, SftpDirectoryBrowser), `ScriptRunner`, `MutagenCliClient`, `SessionCommandLineParser` (reads the sync mode).
- The session editor lets both Alpha and Beta be a local folder ("Browse local folder…") or an SSH path ("Browse SSH server…"), so any combination (local/local, local/SSH, SSH/local, SSH/SSH) is possible for other users.

## Technical Decisions
| Decision | Rationale |
|----------|-----------|
| See task_plan.md "Decisions Made" | Single source for decisions |

## Issues Encountered
| Issue | Resolution |
|-------|------------|
| Resolved: does `mutagen sync reset` leave the session paused? | No for a running or halted session: reset resumes by itself (Mutagen source); it stays paused only if the user had paused it. So `resume` is not part of the sequence. To re-check on a real session in phase 5 |
| Decided: per-session flag location | Option A chosen 2026-11-18 (directive comment line in mutagen-create.bat, syntax and placement in task_plan.md "Decisions Made"). Rejected: (B) per-session overrides in config_mutagenmon.json (two files to keep in sync, orphan on rename, needs a reload), (C) Mutagen `--label` on the create line (editing the session terminates and recreates it, FR-27.4, which resets the ancestor). Global flag: `HaltedOneSidedRootEmptyingAutoFix` (bool, default false) |
| Reference: sample bat structure (MutagenMon.App/mutagen/mutagen-create.bat) | `@echo off`, `rem` header, commented disabled sessions (`rem mutagen sync create …`), blank line, `mutagen sync terminate --all`, one `mutagen sync create` line per session, trailing `rem mutagen sync list`. The loader skips every line starting with `rem ` |
| Risk: a directive can be orphaned when a session line is deleted by hand | Ignored by the reader, warning in the log; Edit (rename) and Delete from the editor keep the block consistent |
| Dropped 2026-11-18: alpha stability delay (`HaltedOneSidedRootEmptyingStabilitySeconds`), its measure and the "alpha not empty" guard | The real test showed that the emptied side is known from the frozen `sync list` output, so there is nothing to wait for |
| Existing mechanism: ConfigLoader.SaveWindowSize edits the config as text (keeps `#` comments) but only handles integer keys | A generic "insert if absent" helper is needed for the string and bool keys |
| Existing behaviour: Save in the session editor terminates and recreates the session (FR-27.4) | A flag stored in the create line would recreate the session on every toggle; a flag-only change must not recreate it |
| Decided: cooldown between two automatic attempts | `HaltedOneSidedRootEmptyingCooldownSeconds`, default 60; stop and notify after 3 consecutive failures |
| Decided 2026-11-18: tray/grid, path guard, notifications, manual action, paused monitoring | See task_plan.md "Decisions Made" |
| To verify: exact status texts of the other two halts (root deletion, root type change) | Not documented verbatim; the display mapping uses the `Halted` prefix, so the exact wording does not matter for it. Check on a real session if possible |
| Decided: the manual-resolution icon is only for the root-emptying halt | The other two halts have not occurred yet and the purge + reset sequence does not address them |
| Decided 2026-11-18: no home-directory restriction | The user's argument: mutagen can mirror a whole `/home` (alpha `C:\Mirror`, beta `/home`). The home warning, the extra confirmation and the auto-fix exclusion are all removed; only the filesystem root is refused. Risk to keep in mind: with the flag on for such a session, an emptied alpha would make the auto-fix purge the whole `/home` content (every user); the protection is the opt-in flag (default off, per session) and, for the manual action, the confirmation dialog, which shows the purged side URL and the file count |
| Decided: text comparison is case-insensitive (StartsWith position from the earlier proposal) | User decision 2026-11-18 |
| Decided 2026-11-18: no waiting; both directions; purge the side that was not emptied | See task_plan.md "Decisions Made". Consequence: the purge of a local working folder (when beta is the emptied side) is destructive and only runs with the flag on |
| Source-code answer on resume (https://raw.githubusercontent.com/mutagen-io/mutagen/master/pkg/synchronization/controller.go, branch master, no version visible; read by a sub-agent, NOT executed here) | `reset`: if a run loop exists it pauses the session, overwrites the ancestor with an empty one, then resumes the session; if the session is paused by the user it only clears the ancestor and leaves it paused. A halted session keeps its loop parked (`<-ctx.Done()`), so reset treats it as running: no explicit `resume` is needed. `resume` does not touch the ancestor; whether it is a no-op on a halted session depends on the Status enum order (not read). The CLI help of `reset` has no long text. The Mutagen docs page only says reset makes Mutagen see the undeleted content as new |
| To verify by test: after purge + reset, the new run loop does not halt again | The body of `oneEndpointEmptiedRoot` was not read; with an empty ancestor it should not trigger, but this is a deduction |
| User observation consistent with the source | The user ran only `mutagen sync reset temp` and synchronization restarted (beta copied to alpha), which matches "reset resumes by itself" |
| Decided 2026-11-18: reset only is rejected, the purge stays (the user's test of `mutagen sync reset temp` copied beta back to alpha, as predicted) | Doc-backed: after `mutagen sync reset`, Mutagen sees the content that exists on only one side as new and propagates it |
| Residual risk accepted by the user: an accidental emptiness (unmounted volume, network share down, mistaken delete) is mirrored to the other side | The "alpha not empty" guard was dropped; the protection is the opt-in flag (default off, per session) |
| Side effect: purge deletes files on the purged side that Mutagen ignores and files created there but not yet synchronised | Accepted for mirror sessions; document in the confirmation dialog |
| Cost: each automatic run wipes the purged side and re-uploads everything | Acceptable for small trees; the cooldown limits repeated runs |
| Portability of the purge: `find -mindepth -xdev -delete` is POSIX (Linux, macOS); not available on Windows OpenSSH servers; Mutagen also has non-SSH transports (e.g. Docker) | Excluded from the auto-fix (proposal, to confirm) |
| Alternative, not chosen: one-way-replica for pure mirrors | Docs do not say whether the check applies to that mode; changes current two-way-safe behaviour |

## Resources
- https://mutagen.io/documentation/synchronization/safety-mechanisms
- requirements/03-tray-icon-requirements.md §3 and §7
- requirements/06-configuration-reference.md
- MutagenMon.Core/Status/SessionStateTracker.cs
- MutagenMon.Core/Monitoring/SessionMonitorService.cs
