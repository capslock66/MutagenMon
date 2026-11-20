# Progress Log

## Session: 2026-11-17

### Current Status
- **Phase:** 5 - Validation (phases 2 to 4 complete; phase 5 not started, waiting for the user's explicit request)
- **Started:** 2026-11-17

### Actions Taken
- Analysis of the halt handling in SessionStateTracker / SessionMonitorService; confirmed with the user's screenshot.
- Checked the Mutagen safety-mechanisms documentation.
- Defined the resolution sequence (alpha-not-empty guard, beta purge, reset, resume) and its guards.
- Created this plan as a separate directory; the in-progress plan `2026-11-17-prompt-macro` and `.planning/.active_plan` were left untouched (pointer restored after init).
- Real-session tests by the user (2026-11-18): reset alone copies the other side back; `sync list` is frozen while halted and shows the emptied side. Plan simplified accordingly (no wait, both directions, purge then reset, no resume, no home restriction).
- Phase 2 done (2026-11-18): new requirements/12-halted-sessions-requirements.md (FR-57 to FR-65); cross-references added to 01 (FR-2.4, FR-3 row, FR-11.5), 03 (Problems row), 06 (3 keys), 07 (FR-17.1, FR-17.4, FR-27.4b), 09 (FR-35.5) and README.
- Spec choices made while writing, not explicitly validated by the user: the manual action works whatever the sync mode; icon kind `DeleteSweepOutline` (to check); purge timeout reuses `ScriptTimeoutSeconds`; "failure" = a step errors or the halt persists/returns with no non-halted poll in between; a halt that cannot be fixed automatically (flag off, paused, mode, E undetermined, P unsupported) is notified; section name "Halted sessions" in the monitor editor; tab "Recovery" and tri-state labels in the session editor.

- Phase 3 done (2026-11-18): Halted classification (code 50), "Synchronizable contents" parsing, red grid row and halt log, config keys with startup auto-add and monitor editor fields, `@halted-auto-fix` directive reader/writer, "Recovery" tab and directive-only Edit; 5 user-test sections added to requirements/UserTests.md (UT-57.1, UT-63.1, UT-63.2, UT-64.1, UT-65.1, all 📋 ⬜).
- Review loop: 3 passes with eortc-dotnet-code-reviewer. Pass 1: 2 important (directive writer not idempotent without a `terminate --all` line; Add/Edit wrote the file twice) + minors, fixed. Pass 2: 1 important (an orphan directive came back to life on Add with "inherit"), fixed. Pass 3: no blocking or important finding; its 3 minors (AddAsync doc, stronger test, extra blank line) fixed.
- Accepted, not changed: a malformed uppercase `REM ... @halted-auto-fix` line can still be read as a session (pre-existing case-sensitive `rem ` check); removing the last directive leaves the blank line the writer added; an Edit with no change may regroup hand-placed directives; the shipped config file lacks the 3 new keys (added at startup); only the plain `mutagen sync terminate --all` form is an anchor.
- Spec amendments made while implementing (requirements/12): FR-57.3 colours, FR-63.2 empty text refused, FR-65.1 Duplicate copies the source flag, FR-65.3 unchanged-command comparison after parse + render.

- Phase 4 done (2026-11-20): new MutagenMon.Core/Recovery (HaltedSessionAnalyzer, EndpointPurger, HaltedRecoveryService, HaltedAutoFixCoordinator), `MutagenCliClient.ResetSessionAsync`, halt notifications, `SessionDefinition.HaltedAutoFix`, red-row "Accept deletion" icon and action in the status window, monitor wiring and reload handling; 8 user tests added (UT-60.1 to UT-60.5, UT-61.1 to UT-61.3, UT-62.1, all 📋 ⬜).
- Review loop (phase 4), 3 passes. Pass 1: 2 blocking (a directive-only Edit never reached the running coordinator; the manual action purged from a snapshot read before the confirmation) + 4 important (cooldown lost between episodes; ssh not killed on timeout and a timeout of 0; local purge on the UI thread with an ineffective timeout; single-flight lost across a reload), fixed. Pass 2: 3 important (killing ssh does not stop the remote `find` -> `-tt`; a recovery started from a listing taken before the previous recovery finished -> `PollStartedUtc` + last-completed time; manual recoveries not awaited by a reload -> `StopAsync`), fixed. Pass 3: no blocking finding, 1 important (`Trim()` on the `pwd -P` output could point to a neighbouring folder -> `TrimEnd('\r')`), fixed together with the trivial minors; these last fixes were not re-reviewed (3-pass limit reached).
- Left as is, reported: `-tt` is not validated against the Windows OpenSSH client (to test; `RedirectStandardInput = true` is the first thing to try if it hangs); a microsecond TOCTOU between `CheckUnchanged` and `RecoverAsync`; a manual recovery interrupted by a reload only shows a warning; no `-o BatchMode=yes`; no visual feedback during a manual recovery; a mount point under an SSH root makes the purge partial; pausing monitoring terminates a halted session while its automatic run is in flight; a halted session raises one "monitoring is paused" notification during a reload.

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Purge + reset + resume on a throwaway session | Beta ends up equal to alpha, no re-halt | Not run | pending |
| `dotnet test MutagenMon.Core.Tests` after phase 3 | All pass | 625 passed, 0 failed, 4 skipped (Windows only) | pass |
| `dotnet build` MutagenMon.Core and MutagenMon.App after phase 3 | 0 error, 0 warning | 0 error, 0 warning | pass |
| UT-57.1, UT-63.1, UT-63.2, UT-64.1, UT-65.1 (manual, Windows) | As written in requirements/UserTests.md | Not run (the WPF UI cannot run on this machine) | pending |
| `dotnet test MutagenMon.Core.Tests` after phase 4 | All pass | 724 passed, 0 failed, 4 skipped (Windows only) | pass |
| `dotnet build` MutagenMon.Core and MutagenMon.App after phase 4 | 0 error, 0 warning | 0 error, 0 warning | pass |
| UT-60.1 to UT-60.5, UT-61.1 to UT-61.3, UT-62.1 (manual, Windows, real SSH server) | As written in requirements/UserTests.md | Not run | pending |

### Errors
| Error | Resolution |
|-------|------------|
