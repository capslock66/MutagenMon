# Task Plan: Scripts tab (remote bash scripts over SSH)

## Goal
Add a "Scripts" tab listing scripts defined in a separate ini-like file. The user selects a script and runs it; the whole script is executed in one SSH connection on a Linux server. First use case: start a detached tmux session running `claude --remote-control`.

## Next Step
Phase 4: Tests and user tests. Not started; waits for an explicit request.

## Current Phase
Phases 1-3 complete; Phase 4 pending

## Phases

### Phase 1: Requirements
- [x] 1.1 Write `requirements/11-scripts-tab-requirements.md` (FR-*, NFR, file format, directives, UI, errors)
- [x] 1.2 Add the document to `requirements/README.md` reading order
- [x] 1.3 Add config keys to `requirements/06-configuration-reference.md`
- **Status:** complete

### Phase 2: Core (MutagenMon.Core)
- [x] 2.1 Parser for the scripts file (sections, `# @key: value` directives)
- [x] 2.2 Config option: path of the scripts file
- [x] 2.3 Script runner: `ssh <host> bash -s`, script on stdin, async, streamed stdout/stderr, exit code, timeout, cancel
- [x] 2.4 Host resolution: system `ssh` (`SshPath`) resolves the alias from `~/.ssh/config` itself; `SshConnectionResolver` (SSH.NET) not used
- **Status:** complete

### Phase 3: UI (MutagenMon.App)
- [x] 3.1 New tab: script list (left), read-only script text + Run/Cancel + Reload button, read-only output pane (right)
- [x] 3.2 Running state, exit code display, confirmation dialog for `@confirm: true`
- [x] 3.3 Logging of executions via existing logger
- **Status:** complete

### Phase 4: Tests and user tests
- [ ] 4.1 Unit tests: parser (including `[ -f x ]` / `[[ ... ]]` not treated as headers), runner with fake ssh
- [ ] 4.2 Update `requirements/UserTests.md`
- [ ] 4.3 Build/test per project on Linux (never the .sln)
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| One SSH connection per script run, whole script on stdin of `bash -s` | Preserves shell state (`cd`, variables); simple; no per-line parsing |
| Execution target defined per script via `# @host: <ssh alias>` | User choice; one file can target several servers |
| Read-only output pane (stdout/stderr + exit code), no embedded terminal | User choice; WPF has no native TTY control |
| Scripts file reloaded on demand (Reload button), no FileSystemWatcher | User choice; simplest |
| `set -e` prefixed by default; `# @stop-on-error: false` disables it | User choice; bash default continues after errors |
| Section header regex strict: whole line `^\[[A-Za-z0-9_.\- ]+\]$`, no space after `[`, no `[[` | Avoid clash with bash `[ ... ]` / `[[ ... ]]` |
| Use `tmux new -d` (detached) in scripts, plus `has-session` guard | No TTY under non-interactive ssh; idempotent re-run |
| Do not couple to polling / tray icon code | Tray icon is the top-priority requirement (03-tray-icon-requirements.md) |
| Work on branch `remote-script` | User request |
| Config keys `ScriptsFile` (default `config/scripts.ini`, changed from `mutagen/scripts.ini` on user request) and `ScriptTimeoutSeconds` (default `300`) | Proposed in Phase 1; default path set by user next to `config_mutagenmon.json`; timeout to be confirmed by user |
| Directives: `@host` (required), `@description`, `@stop-on-error`, `@confirm`; FR-47..FR-54 | Phase 1 document 11 |
| `ssh` runs with `BatchMode=yes`, no TTY | A password prompt could otherwise hang a run |
| Output pane capped at 5000 lines | Avoid memory exhaustion by chatty scripts |

## Open Questions
- Confirm the proposed defaults (300 s timeout, 5000-line cap).

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| (none yet) | | |
