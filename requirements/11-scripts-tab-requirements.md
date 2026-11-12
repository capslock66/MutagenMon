# Scripts Tab — Requirements & Design

This document specifies a status-window tab that lists user-defined bash
scripts, lets the user select one, edit it and run it on a remote Linux
server over SSH, and shows its output.

**Status: implemented.** Continues the FR numbering from
[10-logs-tab-requirements.md](10-logs-tab-requirements.md)'s FR-46, as FR-47
through FR-55 (FR-55 is the macros feature). FR-56 (implemented; the
manual tests UT-56.x are validated) adds the `@session-script` directive (FR-48.5) and the
"run a script on the selected session" action of the Sync tab.

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
  selected) and **Cancel** (enabled only while the script that is running is
  the one displayed: while a script runs, selecting another script or a group
  leaves Cancel disabled, and selecting the running script enables it again;
  it is also disabled when the running script was not started from this tab,
  FR-56.6, and once the cancellation was requested. Exception: when the
  running script is no longer in the list — renamed or removed in the file
  and reloaded — Cancel stays enabled, so the run can always be stopped).
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
  ("Reloading discards them"). **Run** (FR-51.1) and the application exit
  (FR-7.4, after the user confirmed it) save every script with unsaved
  changes first, exactly as **Save** does; when the save is refused (invalid
  name, a rename while a script runs) or fails, a message says so, the edits
  stay as drafts and the run does not start, or the application does not
  exit. Only Save, Run and the confirmed exit save: an automatic restart of
  the application (tray icon recovery) or the end of the Windows session
  closes it without saving, and the pending edits are lost. Save does not
  check the drafts of the other scripts for a line that looks like a
  section header (such a line creates a new section in the file), and Run
  and the exit inherit that.
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
  | `@session-script` | flag, written **without a value** (`# @session-script`) | absent = `false` | Marks the script as a **session script** (FR-56): when the line is present in the script its value is `true`, when it is absent the value is `false`. An explicit `# @session-script: true` or `# @session-script: false` is also accepted, `false` meaning "not a session script"; any other value makes the script invalid (FR-49.2). The flag has no effect on the Scripts tab (the script is listed and run there like any other). |
  | `@group` | free text (case-sensitive) | empty | Group of the script. The list is a tree, one level: collapsed group nodes (shown as "name (count)", in order of first appearance in the file), then the scripts without a group at the top level. A click on a group name opens/closes it; the expanded groups are kept across a reload. ▲/▼ move a script within its group, or a whole group past the neighbouring group (its scripts become contiguous in the file); at either end they do nothing. |

  A directive line is `# @key: value`, except `@session-script`, whose colon
  and value are optional (the whole line `# @session-script` is enough; an
  empty value after the colon, `# @session-script:`, also means `true`). Any
  other text after the key without a colon (`# @session-script true`) is not
  a directive: the line is a plain comment and the script is not a session
  script.
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
  unsaved edits, **Run saves every script with unsaved changes first**
  (FR-47.5) and runs the saved script, so the run uses the edited text,
  parsed like the file (an edited `@host`, `@confirm` or `@stop-on-error`
  applies) and a renamed script runs under its new name; if the save fails
  nothing runs. Run is disabled when the edited text is invalid, when it
  contains a line that looks like a section header, or when the name box is
  invalid (FR-47.4).
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
  pane (not in the streamed output): "Running '<name>' on <host>, N seconds"
  while running (refreshed every second, FR-52.6), then "Exit code N (x.xs)", "Cancelled after x.xs" or "Timed out
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
- FR-52.6: **Elapsed time.** While a script runs, the running text of
  FR-52.3 ("Running '<name>' on <host>, N seconds", "1 second" for one) is
  updated every second in the status line, from "0 seconds" at the start;
  it stops when the run ends and is replaced by the outcome. The same text
  is appended to the title of the status window, after the application name
  and version (`MutagenMon <version>  -  Running '<name>' on <host>, N
  seconds`), so it is visible from any tab; the title returns to
  `MutagenMon <version>` when the run ends, whatever the outcome (also when
  it did not start). After a cancel request the status line keeps
  "Cancelling..." (FR-51.4) while the title keeps counting until ssh has
  exited. The counter shows the real elapsed time (read from a clock, not
  counted from refreshes): it never repeats a value or drifts; if the UI
  is busy for over a second the refresh is delayed and the next value is the
  exact elapsed time. It applies to every run, whichever tab
  started it (FR-56).

## FR-53 — Safety

- FR-53.1: When `@confirm` is `true`, a confirmation ("Run script '<name>'
  on <host>?", buttons Run / Cancel) is shown before running
  (`GenericMessageDialog.ShowConfirm`, same pattern as session deletion).
- FR-53.2: The file is user-authored local configuration, trusted at the
  same level as `~/.ssh/config`. The only external values the application
  interpolates into a script body are those of the explicit macros
  `{{env:…}}`, `{{session:…}}`, `{{reg:…}}`, `{{cred:…}}` and
  `{{secret:…}}` (FR-55), substituted locally at run time; it never
  interpolates anything else (paths, conflict data, ...), and the text in
  the file and in the editor is never modified by the expansion.
- FR-53.3: The file MUST NOT hold secrets (NFR-9): a secret is referenced
  by a macro (`{{env:NAME}}`, `{{reg:…}}`, `{{cred:…}}` or `{{secret:…}}`,
  FR-55) and its value is read or typed at run time and never written to
  the file; other authentication relies on the ambient SSH configuration.

## FR-54 — Logging

- FR-54.1: Each run logs through the application's logger (visible in the
  Logs tab and `mutagenMon.log`): script name, host, start, outcome (exit
  code / cancelled / timed out) and duration at `Information`; a non-zero
  exit code or a failure to start `ssh` at `Warning`/`Error`.
- FR-54.2: Script output is **not** copied into the application log (it may
  be large or sensitive); only the pane shows it.
- FR-54.3: The expanded script text (FR-55) is never logged or displayed;
  logs and the editor only ever show the macro form.

## FR-55 — Macros

A **macro** is a placeholder in the script text, replaced by its value when
the script runs, so that a secret or a context value is not stored in the
scripts file.

- FR-55.1: **Syntax**: `{{type:name}}`, case-sensitive. The token contains no
  line break between `{{` and `}}`; it may sit anywhere in a line, with other
  text around it, and several may appear on one line (e.g.
  `{{session:name}}-{{env:SUFFIX}}`). Text that merely contains `{{`, or
  bash constructs such as `${VAR}` or `$(cmd)`, is not a macro. **A macro in a
  comment is ignored**: everything to the right of a bash comment marker, up
  to the end of the line, is neither expanded, nor checked, nor masked, nor
  asked for by the secret dialog, and stays in the text as written (bash
  ignores it too). A `#` is a comment marker when it begins a word (start of
  the line, or right after a space, a tab, `;`, `&`, `|`, `(`, `)`, `<` or
  `>`; another Unicode space is part of the word) and is not
  inside single or double quotes or escaped by a backslash; `$#`, `${#var}`
  and `a#b` are not comments. Quotes are tracked within one line only: a line
  starting with `#` inside a multi-line quoted string or a here-document is
  seen as a comment, so a macro there is not expanded; conversely, a quote
  closed on a later line is read as an opening quote on that line, so a real
  comment after it is not detected and its macros are expanded and checked.
  The `$'…'` quoting is not recognised either (`\'` ends the quote). The text of a macro is
  never read as a quote or a comment marker (`{{secret:Item #2}}`). The types are
  `env` (FR-55.2), `session` (FR-55.3), `reg` (FR-55.9), `cred` (FR-55.10)
  and `secret` (FR-55.11); an unknown type is an error (FR-55.6), not
  ignored.
- FR-55.2: **`{{env:NAME}}`** is the value of the environment variable `NAME`
  of the **Windows workstation** running MutagenMon, never of the server
  that executes the script. It is read just before each run, from the
  registry (`HKEY_CURRENT_USER\Environment`, then
  `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`;
  the user value overrides the machine value), so a variable created or
  changed after MutagenMon started is seen without a restart. `REG_EXPAND_SZ`
  values are expanded: a `%OTHER%` reference is replaced by the value of that
  variable read the same way (registry, then process environment), a
  reference to an unknown variable stays as written, and a `%` that does not
  open a known reference stays as written (`50% of %HOME%` expands
  `%HOME%`). A reference to a variable that is already being expanded is not
  followed and stays as written (a `Path` defined as `%Path%;C:\tools` gives
  `%Path%;C:\tools`), and the chain of references stops after five levels.
  A value of the process environment is not expanded again. A registry value
  of an unsupported type (`REG_BINARY`, `REG_MULTI_SZ`) is ignored, as if it
  was absent. A variable absent from the registry falls back to the process
  environment of MutagenMon. A registry that cannot be read falls back to the
  process environment too.
- FR-55.3: **`{{session:<property>}}`** is the value of that property of the
  session row selected in the Sync grid (e.g. `{{session:name}}`). Every
  property of the session row is supported; the property names are those of
  the session model behind the Sync grid, matched **ignoring case** (the
  property `Name` is read by `{{session:name}}`), unlike the macro type which
  is case-sensitive. The row is found by its session name: with two sessions
  of the same name (a known case, see UT-1.2) the first row is used. The properties of the session row
  (`SessionSummaryRow`, the model of the Sync grid) are:

  | Property | Value |
  |---|---|
  | `Name` | session name (the Name column) |
  | `Status` | status text (the Status column) |
  | `AlphaUrl` | alpha endpoint URL (the Alpha column) |
  | `BetaUrl` | beta endpoint URL (the Beta column) |
  | `ConflictCount` | number of unresolved conflicts, as a number |
  | `ConflictDisplay` | the Conflict column: the number, empty when there is none |
  | `LastChangedUtc` | last change as ISO 8601 (`2026-11-08T12:30:00.0000000+00:00`), empty when unknown |
  | `LastChangedDisplay` | the "Last changed" column (`yyyy-MM-dd HH:mm:ss`, local time zone, `—` when unknown): prefer `LastChangedUtc` in a script, it does not depend on the workstation time zone |
  | `IconKey` | internal key of the tray icon image of the session (not meant for scripts) |

  The list follows the row model: a property added to it is available
  without a change to the macro code. A session value is context, not a
  secret: it may be empty and is not masked (FR-55.8).
- FR-55.4: **Raw insertion by default**: the value is inserted verbatim, no
  quote added; the script author chooses the quoting around the macro (e.g.
  `mysql -p'{{env:DB_PWD}}'`). A value containing `$`, `` ` ``, `"`, `'`,
  `\`, `!`, spaces or `;` is interpreted by the remote bash according to
  where the macro is written. **Opt-in quoting**: `{{type:name|quote}}`
  inserts the value shell-quoted (single quotes, each `'` written as
  `'\''`); the author then adds no quotes of their own. `|quote` is
  supported by every macro type.
- FR-55.5: A value (from any macro type) containing a line break (`\r`,
  `\n`) or a NUL character is refused, raw or quoted: the run does not
  start and the status line names the offending macro (a line break would
  inject a new command line into the script).
- FR-55.6: **Failure before the run**: when any macro cannot be resolved
  (environment variable not found or empty, unknown session property, a
  `session:*` macro with no session selected, registry value or credential
  not found or empty or of an unsupported type, `secret` left empty, unknown
  type), the run does not start, ssh is not launched and the status line
  shows an error listing every unresolved macro (e.g. `Macro error:
  environment variable 'DB_PWD' is not defined`). The error names the macro,
  never its value. Nothing is substituted silently.
- FR-55.7: **Expansion**: it happens locally, on the text written to the
  standard input of ssh (FR-50.3), after the `@confirm` check. `@confirm`
  (FR-53.1) keeps its usual text (script name and host) and never shows the
  expanded script; when the script uses a `session` macro, it adds the
  resolved session name (`(none)` when no session is selected).
  A secret thus travels inside the encrypted ssh stream, never on a command
  line of the workstation.
- FR-55.8: **Masking**: every value that came from an `{{env:…}}`,
  `{{reg:…}}`, `{{cred:…}}` or `{{secret:…}}` macro is replaced by `***`
  wherever it appears in the output pane (stdout and stderr) and in anything
  logged from the output. The match is exact, best effort: it covers
  `set -x`, `echo` and error messages, not a transformed secret (base64,
  partial value). A value containing a single quote is masked in both its raw
  and its escaped form (each `'` written as `'\''`), with or without `|quote`,
  since `set -x` traces it that way, quoting the whole word (`a'b` is traced as
  `'a'\''b'`, and `-pa'b` as `'-pa'\''b'`). Other forms of the same value are not masked: a secret
  containing a control character such as a tab is traced by bash as `$'a\tb'`,
  and a non-ASCII secret may be traced as octal escapes (`$'p\303\244ss'`) by a
  remote bash running in the `C` locale. The output is decoded as UTF-8: a
  remote shell writing another encoding is not masked. A very short secret
  (one or two characters) is replaced everywhere it appears, which can make the
  output hard to read. Session property values are not masked.
- FR-55.9: **`{{reg:<key>:<value name>}}`** is the data of a value of a
  registry key of the **Windows workstation**, e.g.
  `{{reg:HKCU\Software\MyApp:DbPassword}}`. The text between `reg:` and the
  **last `:`** is the key, the rest is the value name (so a value name cannot
  contain `:`); neither may be empty. Hives: `HKCU` and `HKLM` (the long
  names `HKEY_CURRENT_USER` and `HKEY_LOCAL_MACHINE` are also accepted, the
  hive name being matched ignoring case), any other hive is an error; the 64-bit registry view is read. The value is
  read just before each run. Supported value types: `REG_SZ` (verbatim),
  `REG_EXPAND_SZ` (expanded as in FR-55.2), `REG_DWORD` and `REG_QWORD`
  (unsigned decimal text);
  `REG_BINARY`, `REG_MULTI_SZ` and any other type are an error. A missing
  key or value, an unsupported type or an empty value is unresolved
  (FR-55.6).
- FR-55.10: **`{{cred:<Target>}}`** is the password of the **generic**
  credential named `<Target>` in the Windows Credential Manager of the
  Windows user running MutagenMon, e.g. `{{cred:MyApp/DbPassword}}`. All the
  text after `cred:` is the target name (it may contain `:`, `/` and `\`); an
  empty target is an error. The user name stored with the credential is
  ignored; only the password is used, read as UTF-16 text (what `cmdkey` and
  the Windows Credential Manager write; trailing null characters are
  dropped, and a password of odd size, which cannot be UTF-16, is
  unreadable). It is read just before each run.
  MutagenMon only reads credentials: they are created and edited with the
  Windows Credential Manager or `cmdkey`. A target not found, an empty
  password or an unreadable password is unresolved (FR-55.6). The secret is
  kept encrypted by Windows (DPAPI, bound to the user): this is the
  recommended source for passwords.
- FR-55.11: **`{{secret:<Label>}}`** is a value typed by the user when the
  script runs, never stored. All the text after `secret:` is the label
  (case-sensitive, no line break, not empty); two macros with the same label
  are the same secret. Before ssh is launched, **one single dialog**
  containing a grid asks for the values: one row per distinct label, in order
  of first appearance in the script, each row showing the label and an input
  that hides the typed characters, with OK and Cancel. A label used several
  times is asked once per run. The values are kept in memory for this run
  only: never stored, never logged, never reused by a later run. Cancel (or
  closing the dialog) cancels the run: it does not start and the status line
  says it was cancelled. An empty value is unresolved (FR-55.6). A script
  without a `secret` macro shows no dialog.

## FR-56 — Run a script on the selected session from the Sync tab

*Implemented; the manual tests UT-56.x are validated.* A script carrying the `@session-script` flag
(FR-48.5) is meant to act on a Mutagen session. The Sync tab lets the user
pick such a script and run it on the session selected in the sessions grid,
without going through the Scripts tab.

- FR-56.1: **Toolbar.** The Sync tab toolbar (FR-16) is laid out, from left
  to right, as: **Add**, **Duplicate**, the **script combo** (FR-56.2), the
  **Run** button (FR-56.3), then, aligned to the right edge,
  **Resolve conflicts** and, as the rightmost control, the
  **Stop Mutagen sessions** / **Start Mutagen sessions** toggle (FR-7.2).
  The toggle's label, behaviour and disabled-while-reloading state are
  unchanged: only its position changes, from "immediately right of Add"
  (FR-16.5) to the far right of the toolbar.
- FR-56.2: **Script combo.** It lists the **valid** scripts (FR-49.2) whose
  `@session-script` value is `true`, in file order, shown by name. Scripts
  without the flag, with `: false`, or invalid are not listed. It reads the
  scripts file as saved on disk (the unsaved edits of the Scripts tab are not
  used). The list is rebuilt when the status window is brought to the front
  (shown or activated again, which also picks up an edit of the file made
  outside the application), when a configuration reload ends, and whenever
  the Scripts tab saves, reloads, adds, deletes or moves a script; the selected script stays selected when
  its name is still in the list, otherwise nothing is selected. When the file
  is missing or unreadable, or no script is flagged, the combo is empty and
  disabled and no error is shown (FR-49.2 already reports it in the Scripts
  tab). The choice is kept in memory only.
- FR-56.3: **Run button and F5.** The button sits right of the combo, with
  the same green ▶ icon as the Scripts tab Run button (FR-47.2) and the
  tooltip "Run (F5)". **Run is enabled only when all of the following hold**:
  a session is selected in the sessions grid, a script is selected in the
  combo, and no script is running (FR-56.6). Otherwise it is disabled,
  and so is its shortcut: **F5** on the Sync tab does exactly what a click
  on Run does, and does nothing when Run is disabled. F5 keeps its behaviour
  on the Scripts tab (FR-47.2) and does nothing on the other tabs.
- FR-56.4: **Starting a run.** The session is read once, at the click
  (or key press), like FR-55.3; it is the session the `{{session:…}}`
  macros of the script resolve to. The script is the one parsed from the
  file (FR-48), read again at the click. Then, exactly as on the Scripts tab: the `@confirm`
  confirmation if the script asks for it (FR-53.1, with the
  `Selected session:` line of FR-55.7 when the script uses a `session`
  macro), the macro expansion (FR-55, including the secret dialog), and
  `ScriptRunner` (FR-50, FR-51.2 to FR-51.5). The script runs even when
  it contains no `session` macro.
- FR-56.5: **Run dialog.** The run is shown in a modal dialog owned by the
  status window, titled with the script name and the session name. It
  contains the read-only **output pane** (FR-52.1, FR-52.2, FR-52.4 cap and
  clearing; stderr in red), the **status line** (FR-52.3, error shown in
  red), a **Cancel** button (FR-51.4, enabled only while the script runs)
  and a **Close** button (enabled only once the run has ended). The run
  starts as soon as the dialog opens. The dialog cannot be closed (button,
  title bar, Esc) while the script runs: the user clicks Cancel first and
  closes it when the status line shows the outcome. If the run does not
  start because of a macro error or a cancelled secret dialog (FR-55.6,
  FR-55.11), the dialog still opens, with an empty pane and that message in
  the status line, and Close is enabled. The output of a session run is
  not kept after the dialog closes (no per-script cache, unlike FR-52.4).
- FR-56.6: **One run at a time.** The rule of FR-51.1 spans both tabs: Run is
  disabled on the Sync tab while a script started from the Scripts tab runs,
  and the Scripts tab Run is disabled while the dialog's script runs.
- FR-56.7: **Logging and safety.** FR-54 applies unchanged (the user action
  and the outcome are logged; the script output and the expanded text are
  never logged or shown outside the dialog's pane, FR-54.2, FR-54.3), as do
  FR-53 and the masking of FR-55.8. The run shares no state with polling or
  the tray icon (NFR-1).

## Implementation

- `MutagenMon.Core/Configuration/MutagenMonOptions.cs`: `ScriptsFile`
  (default `config/scripts.ini`) and `ScriptTimeoutSeconds` (default 300).
- `MutagenMon.Core/Scripts/`: `ScriptDefinition` (name, host, description,
  stop-on-error, confirm, group, session-script flag, body, validation error), `ScriptFileParser`
  (FR-48, FR-49.2; pure), `ScriptFileEditor` (FR-47.5, FR-47.6, FR-48.5:
  rewrites, adds, removes and moves sections without touching the rest of
  the file), `ScriptRunner` (FR-50, FR-51; streams output through a
  callback, takes cancellation and timeout; testable with a fake `ssh`) and
  `ScriptRunResult`.
- `MutagenMon.Core/Scripts/` (macros, FR-55; Core stays testable):
  `ScriptMacro` (token parser `Find`, `SecretLabels`), `ScriptMacroExpander`
  (`Expand`, with the values passed as delegates in `ScriptMacroSources`),
  `ScriptOutputMasker` (FR-55.8) and `WindowsMacroValueReader` (the Windows
  readers for `env`, `reg` and `cred`: registry and Credential Manager,
  `null` for anything missing or unreadable, passed to `ScriptMacroSources`
  by the application); `MutagenMon.Core/Ssh/ShellQuoting.cs`: the shell
  quoting shared with `ConflictFileClient` (`|quote`, FR-55.4).
- `MutagenMon.App/ScriptsView.xaml`/`.xaml.cs`: the tab, hosted in
  `StatusWindow.xaml` (second tab); `StatusWindow` routes F5 and shows the
  "Scripts *" header. On Run `ScriptsView` saves the pending edits (FR-51.1), then hands the
  script (the saved one, under its new name if it was renamed) and the session selected in the Sync grid
  (`StatusWindow` provides it) to `ScriptRunCoordinator.RunAsync`; the
  coordinator asks for the `@confirm` confirmation, expands the macros
  (checking the non-secret macros before asking for the secrets) and runs
  the script, and `ScriptsView` shows the output, the status line and
  `Macro error: …` or the cancellation without starting ssh (FR-55.6,
  FR-55.11).
- `MutagenMon.App/ScriptRunCoordinator.cs` (FR-51 to FR-56): the run of one
  script shared by every tab — `Prepare` (the `@confirm` confirmation and the
  macro expansion, FR-53.1, FR-55), `RunPreparedAsync` / `RunAsync`, `Cancel`,
  the outcome and elapsed-time texts (FR-52.3, FR-52.6), `IsRunning` /
  `IsCancelling` (the one-run-at-a-time rule, FR-51.1, FR-56.6) and the
  `RunningChanged` event the tabs use to refresh their buttons. `StatusWindow`
  owns the single instance. What is displayed is the caller's, through
  `ScriptRunCallbacks`.
- `MutagenMon.App/ScriptRunWindow.xaml`/`.xaml.cs` (FR-56.5): the modal run
  dialog of the Sync tab; `MutagenMon.App/ScriptOutputPane.cs`: the output
  pane appending (stderr in red, 5000-line cap) shared with the Scripts tab.
- `MutagenMon.App/SecretValuesDialog.xaml`/`.xaml.cs`: the single dialog
  of the `secret` macros (FR-55.11), one `PasswordBox` row per distinct
  label.
- `MutagenMon.App/config/scripts.ini`: shipped sample file.
- Tests: `MutagenMon.Core.Tests` — `ScriptFileParserTests`,
  `ScriptFileEditorTests`, `ScriptRunnerTests`, `ScriptMacroTests`,
  `ScriptMacroExpanderTests`, `ScriptOutputMaskerTests`, `ShellQuotingTests`,
  `WindowsMacroValueReaderTests` (the tests that need the registry or the
  Credential Manager skip themselves off Windows).
- `requirements/UserTests.md`: UT-47.x through UT-55.x.
