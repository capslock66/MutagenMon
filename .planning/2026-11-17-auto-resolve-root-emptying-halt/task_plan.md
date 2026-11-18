# Task Plan: Handle the "Halted due to one-sided root emptying" status

## Goal
mutagenMon recognises any Mutagen `Halted…` session status, and can resolve the "root emptying" halt (opt-in, per session): it reads from `mutagen sync list` which side was emptied, purges the content of the other side, then runs `mutagen sync reset` (which restarts the session itself; no `resume` needed), without any waiting.

## Next Step
Phase 3: after the user's review of requirements/12-halted-sessions-requirements.md (FR-57 to FR-65), start with the `Halted` classification in SessionStateTracker and the "Synchronizable contents" parsing, tests first. Do not start without an explicit request.

## Current Phase
Phase 3

## Phases

### Phase 1: Analysis & decisions
- [x] Check how the tracker handles an unknown status (SessionStateTracker leaves the code untouched)
- [x] Confirm with the user's screenshot: halted session keeps a green icon
- [x] Check Mutagen docs (safety mechanisms: root emptying, reset, resume)
- [x] Define the resolution sequence and its guards (see Decisions Made and findings.md)
- [x] User test on a real session: `mutagen sync list` stays frozen while halted and shows which side was emptied (2026-11-18)
- **Status:** complete

### Phase 2: Requirements
- [x] Add an FR: recognise every `Halted…` status for display (prefix match) with a dedicated message; the root-emptying auto-fix is triggered only by the configured text `HaltedOneSidedRootEmptying` (case-insensitive, matched at the start of the status)
- [x] Add an FR: determine the emptied side from the "Synchronizable contents" of the two endpoints in `mutagen sync list` (sample and parsing rules in findings.md)
- [x] Specify the tray/grid mapping (decided: every Halted case maps to Problems / `green-error`, the session row turns red) and update requirements/03-tray-icon-requirements.md §3 (tray icon is the top-priority requirement)
- [x] Add an FR for the manual action "Accept deletion" (purge the side that was not emptied + reset, no resume): an icon in the grid's actions column, shown only for a session halted by root emptying (not for root deletion / root type change), with a confirmation dialog (session, purged side URL, file count)- [ ] Add an FR for the automatic resolution behind a flag (default off): session directive else global flag, two-way-safe only, not while monitoring is disabled, both directions, no waiting, cooldown and 3-failure limit
- [x] Add an FR for the halt notifications (effective flag false: notify the problem; fix succeeded: silent; fix failed: notify), including once-per-halt de-duplication (same idea as ConflictNotificationTracker)
- [x] Add the new config keys to requirements/06-configuration-reference.md: `HaltedOneSidedRootEmptying` (text), `HaltedOneSidedRootEmptyingAutoFix` (bool, false), `HaltedOneSidedRootEmptyingCooldownSeconds` (int, 60)
- [x] Specify the startup auto-add of missing keys (requirements/06 and 09) and the new fields in the monitor config editor (text key in "Paths", flag and cooldown)
- [x] Specify the per-session directive (syntax, placement, fallback, tri-state values, rename/delete handling, orphan handling) and the session editor field (new "Recovery" tab, tri-state) in requirements/07, as new FRs
- **Status:** complete

### Phase 3: Halted status recognition
- [ ] Parse `Halted…` in SessionStateTracker: new classification mapped to Problems (no silent fall-through), for all three halts
- [ ] Parse the "Synchronizable contents" counts of each endpoint (directories, files, symbolic links) in the `sync list` parser of SessionMonitorService, tolerant of singular/plural wording; unit tests using the real sample output from findings.md
- [ ] Surface the state in the tray icon, the status grid and the log
- [ ] Make sure the restart logic never terminates/restarts a halted session
- [ ] Unit tests (SessionStateTrackerTests, TrayIconStateResolver tests)
- [ ] New options in MutagenMonOptions with defaults (text, flag false, cooldown 60 s) and the "insert only if absent" helper in ConfigLoader (text/bool/int), called at startup
- [ ] Monitor config editor fields + validation (cooldown must be a whole number) and ConfigLoaderTests
- [ ] Directive reader in SessionDefinitionLoader (strict anchored pattern, tolerant of position, case-insensitive value) with tests
- [ ] Directive writer in SessionEditingService: grouped block above the `terminate --all` anchor (blank-line rule, fallbacks), rename on Edit, removal on Delete, only directive lines touched, with tests
- [ ] Session editor: tri-state field (inherit / true / false) in a new "Recovery" tab; a flag-only change skips terminate + recreate
- **Status:** pending

### Phase 4: Resolution (manual action, then automatic)
- [ ] Endpoint operations abstraction with two implementations: local folder and POSIX SSH server (purge of the root content, root refusal, root path resolution); non-POSIX SSH servers (e.g. Windows OpenSSH) and non-SSH transports are excluded from the auto-fix (to confirm)
- [ ] Root path resolution on an SSH side (relative to the SSH home, `cd -P` + `pwd`) and path guard (refuse only the filesystem root `/` and an empty path; a one-level folder such as `/media` is allowed; for a local Windows folder a drive root such as `C:\` is refused, by analogy, to confirm)
- [ ] Purge of the root content only, never the root itself (SSH: `find <root> -mindepth 1 -xdev -delete` via existing Ssh/ShellQuoting helpers; local: delete the root's children)
- [ ] Identify the emptied side from the parsed counts (only the root directory, 0 files, 0 symbolic links, while the other side has content); do nothing and log if it cannot be determined
- [ ] Sequence: purge the side that was NOT emptied -> `mutagen sync reset` immediately (no `resume`, no waiting), stop at the first failure; check afterwards that the session left the halted status
- [ ] Manual action: icon in the actions column only for a root-emptying halt, red row, confirmation (session, purged side URL, file count)
- [ ] Automatic mode behind the flag: detection by the configured text, two-way-safe only, not while monitoring is disabled, cooldown (60 s default) and stop after 3 consecutive failures, log; notifications per the notification FR (silent on success)- [ ] Unit tests with mocks/subclass seams (SSH, CLI client, local filesystem)
- **Status:** pending

### Phase 5: Validation & delivery
- [ ] Test the sequence on a throwaway session (like `Temp`) before enabling it on real sessions, in both directions (alpha emptied, beta emptied)
- [ ] Check that after purge + reset the session does not halt again
- [ ] Add the user tests to requirements/UserTests.md
- [ ] Build and run the tests (per-project builds on Linux, see memory)
- [ ] Review loop (eortc-dotnet-code-reviewer) before summarising
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| Detect the halt by matching the status against the configurable text `HaltedOneSidedRootEmptying` (default "Halted due to one-sided root emptying") | User decision 2026-11-18: the text lives in the config, so a Mutagen wording change needs no recompilation; other halts (root deletion, root type change) must not trigger a purge |
| Purge the side that was NOT emptied, immediately, then `mutagen sync reset`; no `resume`, no waiting (not reset alone, not purge then resume) | Reset alone copies beta back to alpha (confirmed by the user's own test on `temp`); purge then resume re-halts because the purged side is emptied against the old ancestor while the other is not; an empty ancestor removes the deletion the check looks for. The purge must come BEFORE the reset, because the reset restarts synchronization immediately (Mutagen source, see findings.md) |
| No "alpha not empty" guard and no alpha stability wait (both earlier proposals dropped); the config key `HaltedOneSidedRootEmptyingStabilitySeconds` is removed from the plan | User decision 2026-11-18 after a real test: the emptied side is read from the frozen `sync list` output, so no live check or delay is needed (the user had also declined an alpha check earlier). Residual risk accepted: an accidental emptiness (unmounted volume, network share down, mistaken delete) is mirrored to the other side when the flag is true; the opt-in flag is the protection |
| Flag is opt-in, default off, per-session override (a global flag may only set the default) | The halt is a safety check; auto-purge must be a deliberate choice for sessions where the other side is a mirror |
| Name the action "purge" (not "flush") | `mutagen sync flush` is an unrelated Mutagen command |
| The emptied side is authoritative; the content of the other side is destroyed | The emptied side is the one the user changed (clear then refill); the other side is the stale copy |
| The purged side can be a local folder or a POSIX SSH server, on alpha or on beta | Both directions are handled (see below), and the endpoints can be local or SSH for any user. Two implementations justify an abstraction. Non-POSIX SSH servers and non-SSH transports are excluded from the auto-fix (proposal, to confirm) |
| Config key `HaltedOneSidedRootEmptying` (string) holds the text to search; shown in the monitor config editor, section "Paths" | User decision 2026-11-18 |
| Missing keys are added automatically to config_mutagenmon.json at startup (text key, global flag default `false`, cooldown default `60` seconds); the in-memory default already applies when a key is absent, the write only makes the key visible | User decision 2026-11-18; must keep the `#` comments (reuse the ConfigLoader.SaveWindowSize approach, which edits the text instead of re-serializing) |
| Per-session flag: option A, a directive comment line in mutagen-create.bat, syntax `rem sync --name=<session> : @halted-auto-fix: <true|false>` | User decision 2026-11-18. `rem ` lines are already skipped by SessionDefinitionLoader.TryExtractName, so a directive can never be mistaken for a session line and the .bat stays valid for cmd |
| Directive block is grouped just above the blank line preceding `mutagen sync terminate --all`, with exactly one blank line between the block and that line | User decision 2026-11-18. Fallback when the anchor is missing: before the first session line, else end of file. The reader accepts a directive anywhere in the file (hand edits) |
| Directive values: absent = inherit the global flag `HaltedOneSidedRootEmptyingAutoFix`; `true`/`false` = override; invalid value = ignored with a log warning; directive for an unknown or disabled session = ignored | Tri-state in the session editor; choosing "inherit" removes the line |
| Global flag name `HaltedOneSidedRootEmptyingAutoFix` (bool, default false) | Proposed name, accepted implicitly with option A; same family prefix as the text key |
| A flag-only change in the session editor must not terminate/recreate the session | FR-27.4 currently recreates on every Save, which resets the ancestor; compare the regenerated command with the existing line and rewrite only the directive when they are identical |
| All three Mutagen halts (root emptying, root deletion, root type change) are shown as `Problems` (code 50, icon `green-error`) in the tray and the grid; the session row turns red | User decision 2026-11-18. Only root emptying is ever auto-fixed |
| Retry policy: config key `HaltedOneSidedRootEmptyingCooldownSeconds` (int, default 60, added automatically to the file like the other keys) = minimum delay between two auto-fix runs on the same session; after 3 consecutive failures, stop and notify | User decision 2026-11-18 (proposal accepted) |
| Path guard on the purged side: the only refused path is the filesystem root `/` (and an empty path); a one-level folder such as `/media` is allowed | User decision 2026-11-18: a sync on a Linux `/media` is legitimate; a root sync is impossible anyway, a folder is required. Supersedes the earlier "reject home / too shallow" guard. Drive root for a local Windows folder: proposal by analogy, to confirm |
| Notifications, no `NotifyHaltedAutoFix` key. Effective flag (session directive, else global) false: notify that a session is halted (problem). Effective flag true and fix succeeded: no notification. Fix failed (3 attempts): notify | User decision 2026-11-18. The "halted, nothing automatic" notification applies to all three halts, since root deletion and type change are never auto-fixed |
| Manual resolution: an icon in the grid's actions column, shown only for a session halted by root emptying (the configured text); the row is red | User decision 2026-11-18. Root deletion and root type change are out of scope for the manual action ("never had the problem yet"): display, red row and notification only |
| Auto-fix does not run while monitoring is disabled (paused by the user) | User decision 2026-11-18 (proposal accepted), same rule as the automatic restarts |
| Text comparison is case-insensitive (`OrdinalIgnoreCase`), match at the start of the status text (`StartsWith`, from the accepted proposal) | User decision 2026-11-18 for the case; the position rule comes from the earlier proposal |
| No home-directory restriction: no warning in the session editor, no extra confirmation, no exclusion from the auto-fix | User decision 2026-11-18, replacing all the earlier home rules: mutagen can legitimately mirror a whole home area (e.g. alpha `C:\Mirror`, beta `/home`, every user's changes sent to the PC). The only refused path is the filesystem root (see the path guard) |
| Reset-only (no purge) is rejected; the purge stays | User decision 2026-11-18 after testing `mutagen sync reset temp`: it copies beta back to alpha |
| The emptied side is identified from `mutagen sync list` (the "Synchronizable contents" section of each endpoint): the side reduced to its root directory (0 files, 0 symbolic links) while the other side has content | User test 2026-11-18: while halted the output stays identical whatever is done on either side (the run loop is parked, consistent with Mutagen's source), so it records the state at the moment of the halt. The inverse test (beta emptied) gives the mirror result |
| Both directions are handled: alpha emptied -> purge beta; beta emptied -> purge alpha | User decision 2026-11-18; supersedes "only alpha emptied, the inverse case is never automatic". The purge of a local working folder is destructive: it only runs with the flag on |
| No waiting: purge and reset start as soon as the halt is detected and the emptied side is identified | User decision 2026-11-18. After the reset, synchronization restarts and later changes on the emptied side propagate normally |
| `mutagen sync resume` is not part of the sequence | Mutagen source (controller.go `reset`): on a running or halted session, reset pauses, clears the ancestor and resumes by itself; an explicit resume is only needed if the session was paused by the user, a case the auto-fix does not handle. To check the final state: the session must leave the halted status after the reset |

## Errors Encountered
| Error | Resolution |
|-------|------------|
