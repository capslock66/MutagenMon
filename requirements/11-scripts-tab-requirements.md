# Scripts Tab — Requirements & Design

This document specifies a status-window tab that lists user-defined bash
scripts, lets the user select one, edit it and run it on a remote Linux
server over SSH, and shows its output.

**Status: implemented.** Continues the FR numbering from
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

- FR-47.1: `StatusWindow` has a **"Scripts"** tab, second from the left:
  "Sync", "Scripts", "Mutagen Configuration", "Mutagen monitor
  Configuration", "Logs". While a script has unsaved changes (FR-47.5) the
  tab header shows a bold "Scripts *".
- FR-47.2: Above the toolbar, a link shows the full path of the scripts
  file and opens it in the OS default editor. The toolbar has, in this
  order, **Reload** (FR-49.1), **Add** and **Delete** (FR-47.6), **Save**
  (FR-47.5), **Run** (green ▶, also bound to **F5** while the Scripts tab is
  selected) and **Cancel** (enabled only while a script is running).
- FR-47.3: Layout: the script tree on the left, with ▲/▼ buttons under it
  (FR-48.5); on the right, a **Name** box and the selected script's text
  (both editable, FR-47.4) above the output pane (FR-52) and the status line
  (FR-52.3). A message line above the layout shows the information messages
  of FR-49.2.
- FR-47.4: The script's name and text are editable whenever a script is
  selected (the name box and the text are read-only while nothing is
  selected; the name box is also read-only while a script runs). The name
  must be non-empty, valid under FR-48.1, unique in the file and the script
  must not be running; otherwise the reason is shown in red under the box
  and **Save** is disabled.
- FR-47.5: **Unsaved edits.** Edits of a script are kept as a draft when
  another script is selected, when the list is rebuilt (add, delete, move)
  and are shown until saved. **Save** writes every script with unsaved
  changes (the displayed one and all drafts) to the file in a single write
  through `ScriptFileEditor`: the rest of the file (other scripts, comments
  before the first header, blank lines between sections, encoding and line
  ending style) is left untouched; if any edit fails, nothing is written
  and an error dialog is shown. A renamed script keeps its last run output
  and status. **Reload** with unsaved changes asks for confirmation
  ("Reloading discards them").
- FR-47.6: **Add** appends a new section named `new-script` (or
  `new-script-2`, ... when taken) with `# @host:` set to the first
  configured SSH server (`TODO` when there is none) and
  `# @description: TODO`, creating the file when it does not exist, then
  selects it. **Delete** removes the selected script's section after a
  confirmation dialog (cannot be undone); it is disabled while a script
  runs. File write errors show an error dialog.

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
  | `@group` | free text (case-sensitive) | empty | Group of the script. The list is a tree, one level: collapsed group nodes (shown as "name (count)", in order of first appearance in the file), then the scripts without a group at the top level. A click on a group name opens/closes it; the expanded groups are kept across a reload. ▲/▼ move a script within its group, or a whole group past the neighbouring group (its scripts become contiguous in the file); at either end they do nothing. |

  Directive keys are case-insensitive; when a key appears several times,
  the first occurrence wins. A `@stop-on-error` or `@confirm` value that is
  neither `true` nor `false` (case-insensitive) makes the script invalid
  (FR-49.2).
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

- FR-49.1: The file is read when the tab is first shown, when the user
  clicks **Reload**, and after each Add, Delete, Save or move — not watched
  (no `FileSystemWatcher`). The list is rebuilt; the previous selection is
  kept if a script of the same name still exists.
- FR-49.2: A script with no `@host`, with a duplicate name or with an
  invalid `@stop-on-error`/`@confirm` value is still listed but marked
  invalid with the reason (shown in red under its name in place of the
  description); **Run** is disabled for it. A missing or unreadable file
  shows an informational message in the tab and an empty list, never an
  exception dialog.
- FR-49.3: A running script is unaffected by a Reload: it keeps the body it
  was started with.

## FR-50 — Execution target

- FR-50.1: The script runs on the host named by `@host`, using the system
  `ssh` binary (`SshPath`), which resolves the alias, user and keys itself
  from `~/.ssh/config` and the default keys (the application does not
  resolve the host).
- FR-50.2: Arguments are passed as `ProcessStartInfo.ArgumentList` entries,
  never through a shell command string: `-o BatchMode=yes -T -- <host>
  bash -s` (the `--` keeps a host starting with `-` from being read as an
  option).
- FR-50.3: **One SSH connection per run.** The whole script is written to
  the standard input of a single `ssh <host> bash -s` process, encoded in
  UTF-8 without a BOM. Bash executes
  the lines sequentially, each waiting for the previous one, in one shell
  (so `cd` and variables persist). The application does not drive the
  script line by line.
- FR-50.4: `ssh` runs non-interactively (no TTY, `BatchMode=yes`): no
  password/passphrase prompt can block a run. Scripts that need a TTY must
  detach it themselves (e.g. `tmux new -d`, not `tmux new`).

## FR-51 — Run

- FR-51.1: **Run** is enabled when a valid script is selected and no script
  is running. Only one script runs at a time. When the selected script has
  unsaved edits, the run uses the edited text, parsed like the file (an
  edited `@host`, `@confirm` or `@stop-on-error` applies); Run is disabled
  when the edited text is invalid or contains a line that looks like a
  section header.
- FR-51.2: When `@stop-on-error` is `true` (default), the application
  prepends `set -e` to the body; the first failing command ends the script
  with its exit code. When `false`, bash's default applies (execution
  continues after a failing command).
- FR-51.3: The run is asynchronous: it never blocks the UI thread, the
  polling loop or the tray icon.
- FR-51.4: A run ends when the `ssh` process exits (exit code recorded),
  when the user clicks **Cancel** (the `ssh` process is killed), or when the
  timeout `ScriptTimeoutSeconds` elapses (process killed, run marked
  "timed out"; a value of 0 disables the timeout). **Cancel** shows
  "Cancelling..." immediately, and the ssh process is killed (with its
  process tree) off the UI thread; the run reports "cancelled" once ssh has
  exited (the application waits at most 5 s for it).
  If `ssh` cannot be started, the run ends as "failed to start" with the
  error message.
- FR-51.5: Killing the `ssh` process ends the local connection only;
  processes the script already detached on the server (e.g. a `tmux`
  session) keep running. The UI states this in the Cancel/timeout message.

## FR-52 — Output pane

- FR-52.1: Standard output and standard error of the `ssh` process are
  appended to a read-only pane as they arrive (streamed, not buffered until
  exit), stderr in red.
- FR-52.2: Output is produced on background threads; every UI update is
  marshaled to the UI thread (same constraint as FR-40.3).
- FR-52.3: The outcome of the run is shown in the status line under the
  pane (not in the streamed output): "Running '<name>' on <host>..." while
  running, then "Exit code N (x.xs)", "Cancelled after x.xs" or "Timed out
  after x.xs" (the last two add that processes already detached on the
  server keep running, FR-51.5), or "Failed to start ssh: <error>". A
  non-zero exit code, cancellation, timeout or failure is shown as an
  error.
- FR-52.4: The pane is cleared at the start of each run. The pane is capped
  (last 5000 lines) so a chatty script cannot exhaust memory. The last run
  (output and status) of each script is kept in memory while the
  application runs: selecting another script and coming back shows that
  script's own last run, and a run in progress keeps accumulating its
  output while another script is displayed.
- FR-52.5: There is no embedded terminal. Observing a long-lived session
  (e.g. the tmux session started by the script) is done outside the
  application, or by a script that prints it (`tmux capture-pane -p -t <name>`).

## FR-53 — Safety

- FR-53.1: When `@confirm` is `true`, a confirmation ("Run script '<name>'
  on <host>?", buttons Run / Cancel) is shown before running
  (`GenericMessageDialog.ShowConfirm`, same pattern as session deletion).
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

## Implementation

- `MutagenMon.Core/Configuration/MutagenMonOptions.cs`: `ScriptsFile`
  (default `config/scripts.ini`) and `ScriptTimeoutSeconds` (default 300).
- `MutagenMon.Core/Scripts/`: `ScriptDefinition` (name, host, description,
  stop-on-error, confirm, group, body, validation error), `ScriptFileParser`
  (FR-48, FR-49.2; pure), `ScriptFileEditor` (FR-47.5, FR-47.6, FR-48.5:
  rewrites, adds, removes and moves sections without touching the rest of
  the file), `ScriptRunner` (FR-50, FR-51; streams output through a
  callback, takes cancellation and timeout; testable with a fake `ssh`) and
  `ScriptRunResult`.
- `MutagenMon.App/ScriptsView.xaml`/`.xaml.cs`: the tab, hosted in
  `StatusWindow.xaml` (second tab); `StatusWindow` routes F5 and shows the
  "Scripts *" header.
- `MutagenMon.App/config/scripts.ini`: shipped sample file.
- Tests: `MutagenMon.Core.Tests` — `ScriptFileParserTests`,
  `ScriptFileEditorTests`, `ScriptRunnerTests`.
- `requirements/UserTests.md`: UT-47.x through UT-54.x.
