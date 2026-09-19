# Mutagen Config Editor — Requirements & Design

This document specifies a new capability: viewing and editing mutagen's own
global configuration file, `%USERPROFILE%\.mutagen.yml`, directly from the
status view, instead of requiring the user to open it in an external text
editor.

**Status: implemented.** Core: `MutagenMon.Core/Configuration/MutagenConfigFile.cs`
(path resolution, encoding-preserving load/save) and
`MutagenYamlValidator.cs` (syntax validation, new `YamlDotNet` dependency).
App: `MutagenConfigEditorWindow.xaml`/`.xaml.cs`, wired into the status
view's toolbar (`StatusWindow.xaml`/`.xaml.cs`) and `App.xaml.cs`.
Unit-tested in `MutagenMon.Core.Tests`
(`MutagenConfigFileTests`/`MutagenYamlValidatorTests`).

**Revised (2026-09-19) — hosted as a tab instead of a modal dialog.** As
part of the status view becoming a tabbed control panel (`TABS_UI_PLAN.md`),
`MutagenConfigEditorWindow` (a `Window`, shown via `ShowDialog`) was
extracted into `MutagenConfigEditorView` (a `UserControl`), hosted as the
status window's **"Mutagen Configuration"** tab instead of a separate modal
opened from a toolbar button. FR-29 (the toolbar button) and the
per-editor-instance "Reload config & restart" gating in FR-33 are
superseded by this — see the revision notes under each below. FR-30 through
FR-32 (load/Check/Save behavior) are unchanged.

FR-33 went through a revision after manual testing on Windows
(2026-09-11): the first implementation added `IMutagenCliClient.StopDaemonAsync`/
`StartDaemonAsync` (`mutagen daemon stop`/`start`) behind a "Restart mutagen
daemon" button. Testing showed this had **no effect** on already-running
sessions — mutagen only applies `~/.mutagen.yml`'s defaults when a session
is *created*, not when the daemon process restarts. FR-33 below describes
the corrected design: the button reuses the app's existing FR-7.1 "Reload
config & restart" pathway instead (07-...md §FR-16.1/§FR-7.1), which
terminates and recreates every session — that recreation is what actually
picks up the new config. The `StopDaemonAsync`/`StartDaemonAsync` CLI
methods were removed as dead code.

Decisions below marked **(confirmed)** were settled with the user during the
design discussion that produced this document.

## Relationship to existing requirements

- This is a **different file** from every config file MutagenMon already
  knows about:
  - `config_mutagenmon.json` — MutagenMon's own settings, documented in
    [06-configuration-reference.md](06-configuration-reference.md), read by
    `MutagenMon.Core/Configuration/ConfigLoader.cs`.
  - `%USERPROFILE%\.mutagen` — mutagen's own *data* directory (daemon
    socket, session state), referenced in
    06-configuration-reference.md but not a text file.
  - `%USERPROFILE%\.mutagen.yml` — mutagen's own global/project
    **configuration** file (this document's subject). Referenced only in
    prose today (`SessionCommandLine.cs`,
    [07-session-management-requirements.md](07-session-management-requirements.md)
    §FR-20.5), never read or written by any existing code.
- Adds new functional requirements FR-29 through FR-33, continuing the
  numbering from
  [07-session-management-requirements.md](07-session-management-requirements.md)'s
  FR-28. Kept in its own file rather than appended to `07-...md`, which is
  scoped to session add/edit/delete (mutating `mutagen-create.bat`), not to
  raw editing of an unrelated file.
- This is the application's first feature that writes to a file — every
  other existing feature only reads config or shells out to the `mutagen`
  CLI.

## FR-29 — Toolbar button (superseded — now a tab)

- FR-29.1 (original): The status view toolbar
  ([07-session-management-requirements.md](07-session-management-requirements.md)
  §FR-16) gains a new **"Edit mutagen config"** action (icon + label,
  matching the existing Add button's style), placed immediately to the
  right of **Add** in the toolbar's left `StackPanel`.
- FR-29.2 (original): Clicking it opens the editor window (FR-30) as a modal
  dialog (`ShowDialog`, `Owner` = the status window), following the same
  event-delegation pattern as Add (`StatusWindow` raises a request event;
  `App.xaml.cs` owns constructing and showing the window).
- **Superseded (2026-09-19):** there is no more toolbar button or modal
  dialog. The editor is its own **"Mutagen Configuration"** tab in
  `StatusWindow`, always present alongside "Sync" — opening it is just
  selecting the tab. `StatusWindow` owns constructing the hosted
  `MutagenConfigEditorView` once (in its constructor) and calling
  `Initialize(logger)` on it; there is no per-open request event anymore
  (`EditMutagenConfigRequested` and `App.xaml.cs`'s
  `OnEditMutagenConfigRequested` were removed).

## FR-30 — Editor window: load

- FR-30.1: Window title: `"MutagenMon: Edit mutagen config"`. Body is a
  single large `AcceptsReturn="True"` monospace `TextBox` containing the
  raw file text (no field-by-field parsing — this is a plain text editor,
  unlike the structured session Add/Edit window).
- FR-30.2: The target path is fixed and computed the same way
  `ConfigLoader` expands `%USERPROFILE%`
  (`Environment.ExpandEnvironmentVariables`/
  `Environment.GetFolderPath(SpecialFolder.UserProfile)`) joined with
  `.mutagen.yml`. Not user-configurable.
- FR-30.3: **If the file exists**, its full contents are loaded into the
  text box. The file's encoding MUST be detected at load time (BOM
  sniffing, defaulting to UTF-8 without BOM if none is present) and
  remembered for FR-32's write-back — **(confirmed)** the file's original
  encoding must be preserved, not silently normalized to a fixed encoding.
- FR-30.4: **If the file does not exist**, the editor opens with an empty
  text box **(confirmed)** — the file is NOT created at this point. It is
  only created if the user actually saves (FR-32.2). The window SHOULD
  indicate this state (e.g. a subtitle/status text such as "File does not
  exist yet — it will be created on Save") so the user isn't confused by
  an empty editor.
- **Behavior change from tab hosting (2026-09-19):** FR-30.3/FR-30.4's load
  now happens exactly once, when `MutagenConfigEditorView` is constructed
  (the first time the status window is shown), not every time the user
  opens the editor — since it is a persistent tab rather than a
  freshly-constructed modal per open. An external edit to
  `~/.mutagen.yml` made after that point is not picked up until the app
  restarts.

## FR-31 — YAML validation ("Check")

- FR-31.1: The window has a **"Check"** button, separate from
  Save/Close **(confirmed)**, and a text label below the editor reserved
  for validation errors, rendered in **red**, empty/hidden when there is
  nothing to report.
- FR-31.2: Clicking Check parses the text box's current content as YAML
  (new dependency: a YAML library — **YamlDotNet** is the standard choice
  for .NET, MIT-licensed; parsing/validation logic belongs in
  `MutagenMon.Core`, consistent with `SessionCommandLineParser` living in
  Core rather than the App project).
  - On success, the red error label is cleared/hidden.
  - On failure, the label shows the parser's error message(s), including
    line/column information when the library provides it (YamlDotNet's
    `YamlException` exposes `Start`/`End` marks for this).
- FR-31.3: **Save also runs this same validation** before writing
  (FR-32.1) — Save on invalid YAML is blocked, showing the same red error
  label (as if Check had been clicked), rather than writing a file mutagen
  itself would then fail to parse. This reuses one validation path for
  both the manual Check button and the Save gate, so there is no risk of
  the two disagreeing.
- FR-31.4: Validation is purely syntactic (well-formed YAML). No
  schema-level validation against mutagen's own expected config keys is
  proposed — mutagen itself is the authority on which keys/values are
  actually meaningful, the same reasoning already used for endpoint
  URLs (FR-18.3) and permission values (FR-21.4) elsewhere in the app.

## FR-32 — Save

- FR-32.1: Save first runs FR-31's validation; if it fails, saving is
  blocked (FR-31.3) and the window stays open with the user's text
  untouched.
- FR-32.2: On valid content, the file is written to the FR-30.2 path:
  - If the file already existed, it is overwritten in place using the
    encoding detected at load time (FR-30.3).
  - If the file did not exist (FR-30.4), it is created now, using UTF-8
    without BOM (mutagen's own files use plain UTF-8 YAML).
- FR-32.3: **(confirmed)** No file-locking or external-change/conflict
  detection is performed — if the file was modified on disk by something
  else since it was loaded into the editor, Save simply overwrites it
  (last write wins), the same risk profile as editing it in any plain text
  editor.
- FR-32.4: **(confirmed)** After a successful save that overwrote an
  *existing* file's content, the window shows a note that this change is
  not applied live to already-running sessions (FR-33 gives the user a
  direct way to apply it). No such note is shown for the specific save
  that *creates* the file (FR-30.4) — nothing "already running" to be
  behind yet — but it does appear again on every later save in the same
  window session, once the file exists.
- FR-32.5: **(confirmed, revised for FR-33)** Unlike `SessionEditWindow`,
  Save does **NOT** close the window — only **"Close"** (the sole button on
  the right; there is no separate discard/cancel action, since Save is the
  only thing that ever touches disk and it's always explicit) does. This is
  a deliberate deviation from that window's
  Save-raises-event / caller-closes-window pattern
  (07-...md §FR-27.4.4): FR-33's "Reload config & restart" button needs the
  window to still be open and its state (whether a change was actually
  saved) intact after Save runs, so the user can act on the FR-32.4 note
  immediately without reopening the editor. If the write itself throws
  (e.g. permissions, disk full), an error dialog is shown and the window
  stays open with content intact — this part is unchanged from the
  original design and matches `SessionEditWindow`'s own write-failure
  handling (07-...md §FR-27.4).

## FR-33 — Reload config & restart (apply the change to running sessions) — superseded

- FR-33.1 (original): The window gains a **"Reload config & restart"**
  button in the bottom-left button group, ordered **Check, Save, Reload
  config & restart** (left) / **Close** (right, alone) — **disabled by
  default**, whether the file existed or not when the window opened.
- FR-33.2 (original): The button becomes enabled the first time a
  Save (FR-32) actually **persists a change**: the text box's content at
  Save time differs from the content as of the last successful load/save.
  A Save that writes back byte-for-identical content (nothing was actually
  typed, or it was typed and then undone) does **not** enable it. Once
  enabled in a given window session, it stays enabled until clicked
  (FR-33.3 disables it permanently for the rest of that window's
  lifetime — see there for why).
- **Superseded (2026-09-19):** there is no longer a per-editor "Reload
  config & restart" button. The action lives once, in the toolbar shared
  by every tab of `StatusWindow` (below the `TabControl`, left of "Exit
  mutagen monitor") — the same button the Sync tab used to have in its own
  toolbar. It is a plain, always-available action (enabled whenever a
  reload isn't already in progress, exactly like the Sync tab's own
  reload button was), with no per-tab enable-once-a-change-is-saved gating
  (FR-33.2's condition doesn't apply to a button shared across tabs — this
  editor no longer tracks or needs to track "was a change actually
  persisted since last load"). `MutagenConfigEditorView` no longer raises
  a `ReloadConfigRequested` event at all.
- FR-33.3 (original mechanism, still accurate): the shared button calls the
  same `ReloadConfig()` method as before (07-...md §FR-16.1/§FR-7.1) —
  disables monitoring (terminating every configured session via `mutagen
  sync terminate`) and, once every session has stopped, re-reads
  `mutagen-create.bat` and recreates each one (`mutagen sync create`),
  which is what re-reads `~/.mutagen.yml`'s defaults. Only its *ownership*
  changed (the window-level shared toolbar, not this tab), not what it
  does.
- FR-33.4: **(unchanged)** No success/error dialog is shown by this
  button specifically — `OnReloadReady` (the existing FR-7.1 handler)
  already has its own error handling (a `MessageBox.Show` if the new
  configuration fails to load, keeping the previous configuration active)
  and its own observable outcome (the status view's grid and tray icon
  reflect the reloaded sessions once done).

## Design: editor tab layout (superseded the standalone window layout)

```
┌─ MutagenMon ─────────────────────────────────────────────────────────┐
│ Sync │ Mutagen Configuration │ ...                                   │
├───────────────────────────────────────────────────────────────────────┤
│ [ Check ] [ Save ]                                                    │
│ (subtitle shown only if file doesn't exist yet:                       │
│  "File does not exist yet — it will be created on Save")              │
│ ┌───────────────────────────────────────────────────────────────┐    │
│ │ synchronization:                                               │    │
│ │   defaults:                                                    │    │
│ │     ignoreVCSDirectories: true                                 │    │
│ │                                                                 │    │
│ └───────────────────────────────────────────────────────────────┘    │
│ (red, hidden when empty) Invalid YAML at line 3: mapping values...     │
├─────────────────────────────────────────────────────────────────────┤
│ [⟲ Reload config & restart] [Exit mutagen monitor]           [Close] │
└───────────────────────────────────────────────────────────────────────┘
```

"Check"/"Save" are this tab's own toolbar, at the top. "Reload config &
restart"/"Exit mutagen monitor"/"Close" are the toolbar shared by every
tab, below the `TabControl` — not specific to this tab, and not gated on
whether this tab has unsaved/saved changes (see the FR-33 revision note).
There is no "Close" for this tab specifically — closing the whole status
window is the window-level "Close" in that shared toolbar.

## Status view toolbar sketch (superseded — see FR-29's revision note)

```
┌─ MutagenMon ─────────────────────────────────────────────────────────┐
│ [+ Add] [■ Stop Mutagen sessions]                                    │
├────┬──────┬─────────────────────┬──────────┬──────────┬─────────────┤
```

This was the Sync tab's toolbar right after FR-29 added "Edit mutagen
config" to it as a button; that button no longer exists — the feature is
its own tab now (see `TABS_UI_PLAN.md`).

## Implementation

- `MutagenMon.Core/Configuration/MutagenConfigFile.cs`: `ResolvePath()`
  (FR-30.2), `Load(path)` (BOM-based encoding detection, FR-30.3/FR-30.4)
  and `Save(path, content, encoding)` (FR-32.2). `path` is a parameter
  rather than hard-coded internally so tests round-trip through a temp
  file instead of the real, shared `%USERPROFILE%\.mutagen.yml`. Mirrors
  `ConfigLoader`'s `%USERPROFILE%` idiom but is the app's first *writer*.
- `MutagenMon.Core/Configuration/MutagenYamlValidator.cs`: wraps the new
  `YamlDotNet` package's `YamlStream.Load` (FR-31), catching any exception
  (not just `YamlException` — some malformed inputs trip an internal
  scanner assumption and surface as a plain `InvalidOperationException`
  instead) so a parse failure always turns into an FR-31 error message,
  never an unhandled exception on user-typed free text.
- `MutagenMon.App/MutagenConfigEditorView.xaml`/`.xaml.cs` (a `UserControl`;
  supersedes the earlier `MutagenConfigEditorWindow`, a `Window`): unlike
  `SessionEditWindow`, Save reads/validates/writes directly inside the
  control instead of raising an event for the caller — a local file write
  has no external CLI call that can fail independently of it, so there's
  no async operation for a caller to own; a write failure is caught and
  shown in-place, matching `SessionEditWindow`'s "stay open, keep
  everything typed" behavior on failure. Save also shows the FR-32.4 note
  itself (via `GenericMessageDialog.ShowInfo`, owner resolved with
  `Window.GetWindow(this)`). Hosted directly in `StatusWindow.xaml` as the
  "Mutagen Configuration" `TabItem`; `StatusWindow`'s constructor calls
  `Initialize(logger)` on it once (no per-open request event, no
  `App.xaml.cs` involvement — see FR-29's revision note).
- FR-33 (`Reload config & restart`): now owned entirely by `StatusWindow`'s
  shared bottom toolbar, wired to `App.xaml.cs`'s existing private
  `ReloadConfig()` (FR-7.1) exactly like the Sync tab's reload button used
  to be — `MutagenConfigEditorView` has no `ReloadConfigRequested` event
  and no reload-related state at all. An earlier implementation added
  `IMutagenCliClient.StopDaemonAsync`/`StartDaemonAsync` (`mutagen daemon
  stop`/`start`) behind a per-editor button; both were removed once manual
  testing showed daemon restart alone doesn't apply `~/.mutagen.yml`
  changes to already-running sessions (see the note under Status above).
