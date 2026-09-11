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

## FR-29 — Toolbar button

- FR-29.1: The status view toolbar
  ([07-session-management-requirements.md](07-session-management-requirements.md)
  §FR-16) gains a new **"Edit mutagen config"** action (icon + label,
  matching the existing Add button's style), placed immediately to the
  right of **Add** in the toolbar's left `StackPanel`.
- FR-29.2: Clicking it opens the editor window (FR-30) as a modal dialog
  (`ShowDialog`, `Owner` = the status window), following the same
  event-delegation pattern as Add (`StatusWindow` raises a request event;
  `App.xaml.cs` owns constructing and showing the window).

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

## FR-33 — Reload config & restart (apply the change to running sessions)

- FR-33.1: **(confirmed)** The window gains a **"Reload config & restart"**
  button in the bottom-left button group, ordered **Check, Save, Reload
  config & restart** (left) / **Close** (right, alone) — **disabled by
  default**, whether the file existed or not when the window opened.
- FR-33.2: **(confirmed)** The button becomes enabled the first time a
  Save (FR-32) actually **persists a change**: the text box's content at
  Save time differs from the content as of the last successful load/save.
  A Save that writes back byte-for-identical content (nothing was actually
  typed, or it was typed and then undone) does **not** enable it. Once
  enabled in a given window session, it stays enabled until clicked
  (FR-33.3 disables it permanently for the rest of that window's
  lifetime — see there for why).
- FR-33.3: **(revised — see the note under Status above)** Clicking it
  raises an event handled by `App.xaml.cs`, which calls the exact same
  `ReloadConfig()` method as the status view's own "Reload config" toolbar
  button (07-...md §FR-16.1/§FR-7.1) — **not** a new/separate code path.
  That existing pathway is what's actually needed here: it disables
  monitoring (terminating every configured session via `mutagen sync
  terminate`) and, once every session has stopped, re-reads
  `mutagen-create.bat` and recreates each one (`mutagen sync create`) —
  and it's specifically that *recreation* which re-reads
  `~/.mutagen.yml`'s defaults, not anything about the `mutagen` daemon
  process's own uptime. The button is disabled immediately on click and
  stays disabled for the rest of this window's lifetime: `ReloadConfig()`
  is fire-and-forget (its completion is only observable via the status
  view's own `IsReloadInProgress`-driven UI, which this window isn't wired
  into), so there's no completion signal here to re-enable it on, and
  triggering a second full session-terminate-and-recreate cycle while the
  first is still running would serve no purpose.
- FR-33.4: **(revised)** No new success/error dialog is shown by this
  button — `OnReloadReady` (the existing FR-7.1 handler) already has its
  own error handling (a `MessageBox.Show` if the new configuration fails
  to load, keeping the previous configuration active) and its own
  observable outcome (the status view's grid and tray icon reflect the
  reloaded sessions once done). Duplicating that here, or inventing a
  fixed "reloaded" string the way an earlier design showed "daemon
  restarted", would just be a second, redundant, and easily
  out-of-sync source of truth for the same event.

## Design: editor window layout

```
┌─ MutagenMon: Edit mutagen config ───────────────────────────────────┐
│ (subtitle shown only if file doesn't exist yet:                     │
│  "File does not exist yet — it will be created on Save")            │
│ ┌───────────────────────────────────────────────────────────────┐  │
│ │ synchronization:                                               │  │
│ │   defaults:                                                    │  │
│ │     ignoreVCSDirectories: true                                 │  │
│ │                                                                 │  │
│ │                                                                 │  │
│ └───────────────────────────────────────────────────────────────┘  │
│ (red, hidden when empty) Invalid YAML at line 3: mapping values...   │
│                                                                       │
│ [ Check ] [ Save ] [ Reload config & restart ]            [ Close ]  │
└───────────────────────────────────────────────────────────────────────┘
```

"Reload config & restart" starts disabled; it enables the first time Save
persists an actual change (FR-33.2), and disables again for good once
clicked (FR-33.3). Save does not close the window (FR-32.5) — "Close"
does.

## Status view toolbar sketch (updated)

```
┌─ MutagenMon ───────────────────────────────────────────────────────────────────┐
│ [+ Add] [⚙ Edit mutagen config] [■ Stop Mutagen sessions]  [⟲ Reload config]   │
├────┬──────┬─────────────────────┬──────────┬──────────┬─────────────────────────┤
```

`⚙` stands in for the actual "Edit mutagen config" icon: `PackIconMaterial
Kind="FileDocumentEditOutline"`, confirmed present in the installed
`MahApps.Metro.IconPacks.Material` 6.2.1 package the same way FR-16.3
confirmed its own icons.

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
- `MutagenMon.App/MutagenConfigEditorWindow.xaml`/`.xaml.cs`: unlike
  `SessionEditWindow`, Save reads/validates/writes directly inside the
  window instead of raising an event for the caller — a local file write
  has no external CLI call that can fail independently of it, so there's
  no async operation for a caller to own; a write failure is caught and
  shown in-window, matching `SessionEditWindow`'s "stay open, keep
  everything typed" behavior on failure. Save also shows the FR-32.4 note
  itself (via `GenericMessageDialog.ShowInfo`) instead of deferring to the
  caller, since it no longer closes the window (FR-32.5). Wired into
  `StatusWindow.xaml` (new toolbar button, FR-29.1) and `App.xaml.cs`'s
  `OnEditMutagenConfigRequested`.
- FR-33 (`Reload config & restart`): `MutagenConfigEditorWindow` raises
  `ReloadConfigRequested`, handled by `App.xaml.cs`'s
  `OnEditMutagenConfigRequested` with a one-line `window.ReloadConfigRequested
  += (_, _) => ReloadConfig();` — reusing `App`'s existing private
  `ReloadConfig()` (FR-7.1) as-is, with no new Core code and no new
  `IMutagenCliClient` surface. An earlier implementation added
  `IMutagenCliClient.StopDaemonAsync`/`StartDaemonAsync` (`mutagen daemon
  stop`/`start`) behind this button; both were removed (interface, the two
  `MutagenCliClient` implementations, and the corresponding no-op members
  in the `SessionEditingServiceTests`/`SessionMonitorServiceTests` fakes)
  once manual testing showed daemon restart alone doesn't apply
  `~/.mutagen.yml` changes to already-running sessions (see the note under
  Status above).
