# Plan — macros in Scripts (no password in the scripts file)

Status: **plan validated, nothing implemented.** Validated decisions are in section 7. Open points are in section 8.

## 1. Goal

Scripts (`[name]` sections of the `ScriptsFile`, run through `ssh <host> bash -s`) must be able to use a secret or a context value without storing it in the file. A **macro** is a placeholder in the script text, replaced by its value when the script runs. Two sources:

- an **environment variable of the Windows workstation** running MutagenMon (not an environment variable of the server that executes the script);
- a **property of the session row selected in the Sync grid**.

**Out of scope for this plan:** displaying macros in blue in the editor. It will be a separate request (see section 5).

## 2. Current state (facts from the code and spec)

- `requirements/11-scripts-tab-requirements.md`: FR-50.3 the whole script text is written to the stdin of one `ssh <host> bash -s` process (`MutagenMon.Core/Scripts/ScriptRunner.cs`, `RedirectStandardInput`, UTF-8).
- FR-53.2: "The application never interpolates any external value into a script body." FR-53.3: "The file MUST NOT hold secrets (NFR-9)." → **this feature deliberately relaxes FR-53.2**; the spec must be changed first.
- The script text is shown in `ScriptText`, a plain **`TextBox`** (`ScriptsView.xaml`). `IsReadOnly="True"` in the XAML is only the initial value: `ScriptsView.xaml.cs` sets `ScriptText.IsReadOnly = selected is null`, so **the text is editable whenever a script is selected** (drafts per script via `_drafts`, save through `ScriptFileEditor`, commit d45df5b "scripts are now editable"). The current editor is kept as is. The output pane is a read-only `RichTextBox`; the outcome of a run is shown in `StatusText` (FR-52.3).
- Doc/code mismatch: FR-47.3 still says "the selected script's text (read-only)"; the code is ground truth → FR-47.3 must be updated.
- ssh runs with `BatchMode=yes` (FR-50.4): no password prompt is possible, hence the need.

## 3. Syntax (validated)

`{{type:name}}`. The token itself must not contain a line break between `{{` and `}}`; it can sit anywhere in a line, with other text around it and several per line, e.g. `echo "script for the session {{session:name}}"` or `{{session:name}}-{{env:SUFFIX}}`. Case-sensitive. Supported types:

| Macro | Value |
|---|---|
| `{{env:MY_DB_PASSWORD}}` | value of the environment variable `MY_DB_PASSWORD` of the **Windows workstation** (see 4.2) |
| `{{session:<property>}}` | value of that property of the session row selected in the Sync grid, e.g. `{{session:name}}`. **Every property of the session row is supported**; the exact list and property names are enumerated from the session model behind the Sync grid at implementation time and written into FR-55. |
| `{{env:NAME|quote}}`, `{{session:name|quote}}` | same value, **shell-quoted** (opt-in, see 4.4) |

Why `{{ }}`: it does not collide with bash `${VAR}`, `$(cmd)`, `[ ]`/`[[ ]]`, or with the `[section]` header rule (FR-48.1/48.3). Unknown types or unknown session properties are **errors**, not ignored.

## 4. Behaviour at run time

1. **Expansion happens locally, just before `RunAsync`**, on the text written to ssh stdin. The file and the displayed text are never modified.
2. **Source of `env` values: the Windows workstation, read just before each run.**
   - Variables are read **from the registry at each run**, so a variable created or changed after MutagenMon started is seen without restarting the app: `HKEY_CURRENT_USER\Environment` first, then `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment` (user value overrides machine value, as Windows does).
   - `REG_EXPAND_SZ` values are expanded (`%OTHER%` references).
   - A variable not found in the registry falls back to the **process environment** (covers variables injected when MutagenMon was launched, e.g. from a terminal).
   - These are never the environment variables of the remote server.
   - Registry access is Windows-only. `MutagenMon.Core` tests run on Linux, so the expander receives a plain delegate `Func<string, string?>`; the registry reader is a small separate piece that returns `null` on non-Windows. It is not unit-tested on Linux (manual scenario).
3. **Failure before starting ssh.** If any macro cannot be resolved (variable not found/empty, unknown session property, or a `session:*` macro with no session selected), **the run does not start** and an error message is shown in the status line (`StatusText`), listing every unresolved macro, e.g. `Macro error: environment variable 'DB_PWD' is not defined`. Nothing is substituted silently (a missing password must not run half a script).
4. **Insertion is raw by default (validated): no quote is added.** The value is inserted verbatim, because some bash commands break when quotes are added. The script author decides the quoting around the macro, e.g. `mysql -p'{{env:DB_PWD}}'`. Consequence (documented in FR-55): a value containing `$`, `` ` ``, `"`, `'`, `\`, `!`, spaces or `;` is interpreted by the remote bash according to where the macro is written.
   - **Opt-in quoting:** `{{…|quote}}` inserts the value shell-quoted (single quotes, `'` → `'\''`, same helper as `ConflictFileClient.ShellQuote`, to be shared in Core). With `|quote`, the author must not add their own quotes.
   - **Refused values:** if a value (env or session property) contains a line break (`\r`, `\n`) or a NUL character, the run does not start and the status shows an error naming the macro (a line break would inject a new command line into the script). This applies to raw and quoted macros.
5. **Where the secret travels:** inside the ssh stdin stream (encrypted), never on a command line, so it does not show in the local process list. On the server it is visible in `ps` only if the script itself puts it in a command argument (`mysql -pSECRET`); documentation recommends `export`/here-doc/`MYSQL_PWD`-style mechanisms.
6. **Never log or display the expanded text.** `ScriptRunner` logs and the editor keep showing the macro form (FR-54).
7. **Masking (validated):** every value that came from an `env` macro is replaced by `***` wherever it appears in the output pane (stdout and stderr), and in anything logged from the output. Exact-match, best effort: it protects `set -x`, `echo`, and error messages, but not a transformed secret (base64, partial value). Session property values are not masked. Empty values never reach this step (they are errors).
8. `@confirm` (FR-53.1) shows the **unexpanded** script and the resolved session name only.

## 5. Design (Core stays testable, NFR-11)

- `MutagenMon.Core/Scripts/ScriptMacro.cs` — record + regex parser `Find(text) → list of (start, length, type, name, quote)`. Pure. Used by the expander now and reusable by a future colouring feature. Regex must be commented per the project convention.
- `MutagenMon.Core/Scripts/ScriptMacroExpander.cs` — `Expand(text, environment, selectedSession) → (expandedText, errors, secretValuesToMask)`. No new abstraction: a `Func<string, string?>` delegate and the selected session record are passed in (tests adapt to code, no real environment needed).
- Windows registry reader for environment variables (HKCU then HKLM, `REG_EXPAND_SZ` expansion, process-environment fallback), isolated and returning `null` off Windows; its function is what the app passes as the `environment` delegate.
- `ScriptRunner.RunAsync` receives the already expanded text plus the values to mask; output lines go through a masker before reaching `onOutput`/logs.
- `MutagenMon.App/ScriptsView`: provides the selected Sync session, calls the expander on Run, shows errors in `StatusText`, never starts the run on error.
- **Blue display: deferred to a separate request.** The editor stays the current `TextBox`. Note for that future request: a `TextBox` cannot colour part of its text and the text is editable, so it will need either AvalonEdit (new public NuGet, to be confirmed with the user per `nugets.md`; `ScriptText.Text` usages for drafts/change detection/save would move to `Document.Text`) or a fragile overlay. `ScriptMacro.Find` already provides the spans to colour.

## 6. Spec and test changes

- `requirements/11-scripts-tab-requirements.md`: reword FR-53.2/FR-53.3 (only the `{{env:…}}`/`{{session:…}}` macros are interpolated; the file still holds no secret), add a new FR-55 "Macros" (syntax, env source and registry lookup, session property list, raw insertion and `|quote`, refused values, failure before run shown in the status, masking), fix FR-47.3 (the text is editable, not read-only).
- `requirements/UserTests.md`: scenarios for env macro (including a variable created after MutagenMon started), session macro with and without a selected session, undefined variable (error in the status, no run), unknown session property, value containing a line break (refused), `|quote` with a value containing `'` / `$(...)`, masking of an env value echoed by the script.
- Unit tests: parser (adjacent macros, several per line, `|quote`, unknown type, text that merely contains `{{` or bash `${}`, token split across lines not matched), expander (raw insertion verbatim, `|quote` quoting `a'b$(id)`, missing variable, no session, every session property, line break/NUL refused), masker, `ScriptRunner` with the fake ssh (stdin received is expanded, logs contain the macro form, output masked).
- Mandatory `eortc-dotnet-code-reviewer` loop after implementation.

## 7. Decisions validated

1. Syntax `{{type:name}}`, with types `env` and `session`.
2. `session` exposes **all** properties of the selected Sync row.
3. The current editor is kept; the blue colouring will be a separate request.
4. This plan file (`plan-script-macros.md`) is kept.
5. **No quote added by default** (raw insertion); `|quote` available as opt-in.
6. A value containing a line break or NUL is refused (run does not start, error in the status).
7. **Env values are masked** (`***`) in the output.
8. `env` = environment variables of the **Windows workstation**, not of the server.
9. An unknown/undefined macro (variable, session property, missing session) shows an error message in the status and the run does not start.
10. Environment variables are read **from the registry just before each run** (HKCU, then HKLM), with the process environment as fallback.

## 8. Still open

- Names and list of the session properties: to be enumerated from the session model at implementation time.
- Whether the status error should also be written to the output pane (default: status line only).
