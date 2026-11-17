# Task Plan: Handle the "Halted due to one-sided root emptying" status

## Goal
mutagenMon recognises any Mutagen `Halted…` session status, and can resolve the "root emptying" halt (opt-in, per session) by purging the beta root content over SSH, then `mutagen sync reset` and `mutagen sync resume`.

## Next Step
Phase 2: write the requirements (new FR for `Halted` status handling + tray icon rule) in `requirements/` before touching code.

## Current Phase
Phase 2

## Phases

### Phase 1: Analysis & decisions
- [x] Check how the tracker handles an unknown status (SessionStateTracker leaves the code untouched)
- [x] Confirm with the user's screenshot: halted session keeps a green icon
- [x] Check Mutagen docs (safety mechanisms: root emptying, reset, resume)
- [x] Define the resolution sequence and its guards (see Decisions Made and findings.md)
- **Status:** complete

### Phase 2: Requirements
- [ ] Add an FR: recognise `Halted…` statuses (prefix match, never the full sentence) with a dedicated state/message
- [ ] Decide the status code / tray icon mapping and update requirements/03-tray-icon-requirements.md §3 (tray icon is the top-priority requirement)
- [ ] Add an FR for the manual action "Accept deletion" (purge beta + reset + resume) with a confirmation dialog
- [ ] Add an FR for the automatic resolution behind a flag (default off)
- [ ] Add the new config key(s) to requirements/06-configuration-reference.md
- **Status:** pending

### Phase 3: Halted status recognition
- [ ] Parse `Halted…` in SessionStateTracker (new classification, no silent fall-through)
- [ ] Surface the state in the tray icon, the status grid and the log
- [ ] Make sure the restart logic never terminates/restarts a halted session
- [ ] Unit tests (SessionStateTrackerTests, TrayIconStateResolver tests)
- **Status:** pending

### Phase 4: Resolution (manual action, then automatic)
- [ ] Beta root path resolution on the server (relative to the SSH home, `cd -P` + `pwd`) and path guards (reject `/`, home, too shallow)
- [ ] Purge of the beta root content only (`find <root> -mindepth 1 -xdev -delete`), via existing Ssh/ShellQuoting helpers
- [ ] Alpha guard: local path, exists, NOT empty (skip and re-evaluate on the next poll otherwise)
- [ ] Sequence purge -> `mutagen sync reset` -> `mutagen sync resume`, stop at the first failure
- [ ] Manual action with confirmation (session, host, path, file count)
- [ ] Automatic mode behind the flag: strict detection, mode two-way-safe only, one attempt per halt with cooldown, log + notification
- [ ] Unit tests with mocks/subclass seams (SSH, CLI client, alpha filesystem)
- **Status:** pending

### Phase 5: Validation & delivery
- [ ] Test the sequence on a throwaway session (like `Temp`) before enabling it on real sessions
- [ ] Add the user tests to requirements/UserTests.md
- [ ] Build and run the tests (per-project builds on Linux, see memory)
- [ ] Review loop (eortc-dotnet-code-reviewer) before summarising
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| Detect on the prefix `Halted` plus the text `root emptying`; never match the full sentence | The real text is "one-sided", the user quoted "one-side"; other halts (root deletion, root type change) must not trigger a purge |
| Purge beta then reset then resume (not reset alone, not purge then resume) | Reset alone resurrects beta's stale files into alpha; purge then resume re-halts because beta is emptied against the old ancestor while alpha is not; an empty ancestor removes the deletion the check looks for |
| Alpha guard is "alpha is NOT empty" | The developer clears then refills alpha, so alpha is usually partly filled; an empty alpha means a possible unmounted volume or an intended deletion: never destroy beta in that case |
| Flag is opt-in, default off, per-session override (a global flag may only set the default) | The halt is a safety check; auto-purge must be a deliberate choice for sessions where beta is a mirror |
| Name the action "purge" (not "flush") | `mutagen sync flush` is an unrelated Mutagen command |
| The procedure makes alpha authoritative | Beta content is destroyed; sessions where beta must win are out of scope |
| Never automate for a non-local alpha | The alpha emptiness guard needs a local filesystem check |

## Errors Encountered
| Error | Resolution |
|-------|------------|
