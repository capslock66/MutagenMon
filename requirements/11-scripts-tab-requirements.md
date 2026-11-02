# Scripts Tab — Requirements & Design

This document specifies a new capability: a fifth status-window tab that
lists user-defined bash scripts, lets the user select one and run it on a
remote Linux server over SSH, and shows its output.

**Status: planned (not implemented).** Continues the FR numbering from
[10-logs-tab-requirements.md](10-logs-tab-requirements.md)'s FR-46, as FR-47
through FR-54.

Motivating use case: from the Windows machine, start a detached `tmux`
session on a Linux server that runs `claude --remote-control`, so the Claude
session stays alive after the SSH connection is closed and is driven from
claude.ai / the mobile app.

This feature MUST NOT interfere with polling, status computation or the tray
icon ([03-tray-icon-requirements.md](03-tray-icon-requirements.md)): script
execution is fully asynchronous and shares no state with them (NFR-1).

## FR-47 — Tab

- FR-47.1: `StatusWindow` gains a fifth tab, **"Scripts"**, alongside "Sync",
  "Mutagen Configuration", "Mutagen monitor Configuration" and "Logs".
- FR-47.2: The tab's toolbar has the actions **Run**, **Cancel** (enabled
  only while a script is running) and **Reload** (FR-49.3).
- FR-47.3: Layout: script list on the left; on the right, the selected
  script's text (read-only) above a read-only output pane (FR-52).

## FR-48 — Scripts file format

The scripts live in a separate ini-like text file (path: `ScriptsFile`, see
[06-configuration-reference.md](06-configuration-reference.md)).

- FR-48.1: A script starts at a **section header**: a whole line, trimmed,
  matching `^\[[A-Za-z0-9_.\-][A-Za-z0-9_.\- ]*\]$`. The text between the
  brackets is the script name, unique within the file (duplicates: FR-49.2).
- FR-48.2: Every following line, up to the next section header or end of
  file, is the script body, sent verbatim to bash (blank lines and comments
  included). Leading/trailing blank lines of a body are trimmed.
- FR-48.3: A line is **never** a section header when it has a space right
  after `[`, or starts with `[[`. This keeps bash tests (`[ -f x ]`,
  `[[ -n "$x" ]]`) inside a body from being misparsed.
- FR-48.4: Lines before the first section header are ignored.
- FR-48.5: **Directives** are comment lines of the form
  `# @key: value` inside a body, recognised only for the keys below; they
  stay part of the body sent to bash (bash ignores them as comments).
  Unknown `@keys` are ignored.

  | Directive | Values | Default | Meaning |
  |---|---|---|---|
  | `@host` | an SSH alias or `Host` from `SshServers` | — (required) | Server the script runs on (FR-50.1). |
  | `@description` | free text | empty | Shown under the name in the list. |
  | `@stop-on-error` | `true` / `false` | `true` | Whether `set -e` is prepended (FR-51.2). |
  | `@confirm` | `true` / `false` | `false` | Ask for confirmation before running (FR-53.1). |

- FR-48.6: Line endings: both `\r\n` and `\n` are accepted in the file;
  the body is always sent to bash with `\n` only (a stray `\r` breaks bash).

Example:

```
[appman-remote-control]
# @host: robbie
# @description: Start Claude remote control in a detached tmux session
cd sources
cd appman
CCD="$HOME/.claude/remote/ccd-cli/$(ls -1 "$HOME/.claude/remote/ccd-cli" | sort -V | tail -1)"
echo "$CCD"
"$CCD" --version
tmux has-session -t appman-rc 2>/dev/null || tmux new -d -s appman-rc "$CCD --remote-control robbie-appman-rc"
```

## FR-49 — Loading

- FR-49.1: The file is read when the tab is first shown and when the user
  clicks **Reload** — not watched (no `FileSystemWatcher`). The list is
  rebuilt; the previous selection is kept if a script of the same name still
  exists.
- FR-49.2: A script with no `@host`, or with a duplicate name, is still
  listed but marked invalid with the reason; **Run** is disabled for it.
  A missing or unreadable file shows an informational message in the tab
  and an empty list, never an exception dialog.
- FR-49.3: A running script is unaffected by a Reload: it keeps the body it
  was started with.

## FR-50 — Execution target

- FR-50.1: The script runs on the server named by `@host`, using the
  system `ssh` binary (`SshPath`) and the connection settings resolved from
  `~/.ssh/config` / default keys by the existing SSH resolution
  (`SshConnectionResolver`), exactly like the other SSH features (FR-9, FR-18).
- FR-50.2: Arguments are passed as `ProcessStartInfo.ArgumentList` entries,
  never through a shell command string.
- FR-50.3: **One SSH connection per run.** The whole script is written to
  the standard input of a single `ssh <host> bash -s` process. Bash executes
  the lines sequentially, each waiting for the previous one, in one shell
  (so `cd` and variables persist). The application does not drive the
  script line by line.
- FR-50.4: `ssh` runs non-interactively (no TTY, `BatchMode=yes`): no
  password/passphrase prompt can block a run. Scripts that need a TTY must
  detach it themselves (e.g. `tmux new -d`, not `tmux new`).

## FR-51 — Run

- FR-51.1: **Run** is enabled when a valid script is selected and no script
  is running. Only one script runs at a time.
- FR-51.2: When `@stop-on-error` is `true` (default), the application
  prepends `set -e` to the body; the first failing command ends the script
  with its exit code. When `false`, bash's default applies (execution
  continues after a failing command).
- FR-51.3: The run is asynchronous: it never blocks the UI thread, the
  polling loop or the tray icon.
- FR-51.4: A run ends when the `ssh` process exits (exit code recorded),
  when the user clicks **Cancel** (the `ssh` process is killed), or when the
  timeout `ScriptTimeoutSeconds` elapses (process killed, run marked
  "timed out").
- FR-51.5: Killing the `ssh` process ends the local connection only;
  processes the script already detached on the server (e.g. a `tmux`
  session) keep running. The UI states this in the Cancel/timeout message.

## FR-52 — Output pane

- FR-52.1: Standard output and standard error of the `ssh` process are
  appended to a read-only pane as they arrive (streamed, not buffered until
  exit), stderr visually distinguished.
- FR-52.2: Output is produced on background threads; every UI update is
  marshaled to the UI thread (same constraint as FR-40.3).
- FR-52.3: When the run ends, a final line shows the outcome: exit code,
  "cancelled" or "timed out", and the duration.
- FR-52.4: The pane is cleared at the start of each run. The pane is capped
  (last 5000 lines) so a chatty script cannot exhaust memory.
- FR-52.5: There is no embedded terminal. Observing a long-lived session
  (e.g. the tmux session started by the script) is done outside the
  application, or by a script that prints it (`tmux capture-pane -p -t <name>`).

## FR-53 — Safety

- FR-53.1: When `@confirm` is `true`, a Yes/No confirmation naming the
  script and host is shown before running (`GenericMessageDialog.ShowConfirm`,
  same pattern as session deletion).
- FR-53.2: The file is user-authored local configuration, trusted at the
  same level as `~/.ssh/config`. The application never interpolates any
  external value (session names, paths, conflict data) into a script body.
- FR-53.3: The file MUST NOT hold secrets (NFR-9); authentication relies on
  the ambient SSH configuration.

## FR-54 — Logging

- FR-54.1: Each run logs through the application's logger (visible in the
  Logs tab and `mutagenMon.log`): script name, host, start, outcome (exit
  code / cancelled / timed out) and duration at `Information`; a non-zero
  exit code or a failure to start `ssh` at `Warning`/`Error`.
- FR-54.2: Script output is **not** copied into the application log (it may
  be large or sensitive); only the pane shows it.

## Implementation (planned)

- `MutagenMon.Core/Configuration/MutagenMonOptions.cs`: add `ScriptsFile`
  and `ScriptTimeoutSeconds`.
- `MutagenMon.Core/Scripts/` (new): `ScriptDefinition` (name, host,
  description, stop-on-error, confirm, body, validation error),
  `ScriptFileParser` (FR-48, FR-49.2; pure, testable), `ScriptRunner`
  (FR-50, FR-51; streams output via a callback/event, takes cancellation,
  timeout; testable with a fake `ssh`, like `ConflictFileClient`).
- `MutagenMon.App/ScriptsView.xaml`/`.xaml.cs` (new): the tab, hosted in
  `StatusWindow.xaml` and initialised the same way as `LogsView`.
- `MutagenMon.App/mutagen/scripts.ini` (new): shipped sample file with the
  example above.
- Tests: `MutagenMon.Core.Tests` — parser (headers, `[ -f x ]` / `[[ ... ]]`
  inside a body, directives, CRLF, duplicates, missing host) and runner
  (stdin content, `set -e` prefix, exit code, cancel, timeout).
- `requirements/UserTests.md`: new UT entries for FR-47 through FR-54.
