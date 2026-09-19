# Tabbed UI Plan

Analysis and phased plan for converting `StatusWindow` from a single-view
status window into a tabbed control panel with four tabs: Sync, Mutagen
Configuration, Mutagen monitor Configuration, and Logs.

This file tracks the plan only — implementation happens one phase (one tab)
at a time, on explicit request. This file is deleted once implementation is
complete, with confirmation, never automatically.

## Goal

Restructure `StatusWindow` around a `TabControl` with four tabs, each with
its own top toolbar, plus one toolbar shared by all tabs, placed below the
`TabControl`.

## Architectural impact

`StatusWindow` today is a single-purpose status view (NFR-7: no main
window, dialogs on demand — it hides rather than closes). `MutagenConfigEditorWindow`
is a separate modal dialog, opened from the "Edit mutagen config" button.

Moving to tabs does not break NFR-7 (still nothing shown at startup, still
opened on demand from the tray icon), but turns `StatusWindow` into a real
control panel with four facets instead of one status view. This requires:

- Replacing `StatusWindow.xaml`'s root `Grid` with a `TabControl` (four
  `TabItem`s) plus a shared bottom toolbar.
- Extracting `MutagenConfigEditorWindow`'s current content (text editor +
  Check/Save/Reload) into a reusable `UserControl`, hosted in tab 2 — the
  modal window and the "Edit mutagen config" button/`OnEditMutagenConfigRequested`
  in `App.xaml.cs` are removed.
- Creating a new `UserControl` for tab 3 (no UI exists today for editing
  `config_mutagenmon.json`).
- Creating a new `UserControl` for tab 4, plus a new piece of
  infrastructure: an in-memory log buffer (`FileLoggerProvider` today only
  writes to file/Event Log, with no in-memory history).

## Shared bottom toolbar

The "Reload config & restart" button is no longer duplicated per tab: it
becomes a single action in a toolbar shared by all tabs, below the
`TabControl` (so visible regardless of the active tab), positioned to the
left of "Exit mutagen monitor". It always calls the same `ReloadConfig()`
already shared in `App.xaml.cs`, no matter which tab is active.

Consequence: the top toolbars of tabs 1, 2, and 3 each lose their own
"Reload config" button.

Resolved: **Close** also moves to the shared bottom toolbar (right-aligned,
opposite Reload config/Exit). **Cancel** and **Resolve conflicts** stay
inside the Sync tab's own content (below the grid), since they're specific
to that tab's conflicts state.

## Tab 1 — "Sync" (existing content, reused)

Reuses `StatusWindow`'s current content (sessions grid, conflicts section,
Close/Cancel/Resolve conflicts/Exit — placement per the open point above).

Top toolbar: **Add**, **Stop/Start Mutagen sessions** only — "Edit mutagen
config" moves to tab 2, "Reload config" moves to the shared bottom toolbar.

## Tab 2 — "Mutagen Configuration" (existing content, reused)

Reuses `MutagenConfigEditorWindow`'s current content (raw text editor,
"file does not exist yet" notice, validation error text) as an extracted
`UserControl`. Same behavior as specified in
`requirements/08-mutagen-config-editor-requirements.md` — no logic change.

Top toolbar: **Check**, **Save** only — "Reload config" moves to the shared
bottom toolbar.

## Tab 3 — "Mutagen monitor Configuration" (new, structured form)

Edits `config_mutagenmon.json` (schema in
`requirements/06-configuration-reference.md`) via a structured form
instead of raw text:

- Simple typed fields: `MinLogLevel` (combo of `LogLevel` values);
  `DebugExceptionsToConsole` / `StartEnabled` / `NotifyRestartConnection` /
  `NotifyConflicts` / `NotifyAutoresolve` / `NotifyMutagenProfileUpdate`
  (checkboxes); `MergePath` / `ScpPath` / `SshPath` / `MutagenPath` /
  `TrayTooltip` / `LogPath` / `MutagenSessionsBatFile` / `MutagenProfileDir`
  (text fields); `SessionMaxErrors` / `SessionMaxNoSession` /
  `SessionMaxDuplicate` / `MutagenPollPeriodMs` / `MutagenProfileGraceSeconds`
  / `AutoResolveHistoryAgeSeconds` (numeric fields). `DebugLevel` can be
  omitted from the form (legacy, no effect) but must be preserved as-is in
  the JSON if already present, so the value isn't lost on Save.
- A sub-form for `StatusMaxLag` (4 numeric fields: Info/Warning/Error/Restart).
- A list editor for `AutoResolve` (add/remove/reorder rows of
  `{filepath, resolve}`, `resolve` as a combo limited to `"A wins"`/`"B wins"`)
  — the heaviest part of this form, since rule order is significant
  (FR-10.2).
- **Check** validates every field (types/ranges) without writing to disk;
  **Save** serializes to JSON via `MutagenMonOptions` (`System.Text.Json`).

Top toolbar: **Check**, **Save** only — "Reload config" moves to the shared
bottom toolbar.

### Resolved: comment loss on save

The legacy config file tolerates `#`-prefixed whole-line comments, stripped
before parsing (not valid JSON). A structured form serializes clean JSON,
so those comments are lost the first time this tab saves the file — there
is no portable way to keep them (JSON has no native comment syntax, and a
`#`/`//` convention on write would just reinvent a non-standard format).
**Accepted tradeoff, not a bug.**

Consequence: the documentation those comments carried (per-key description,
unit, default — see `requirements/06-configuration-reference.md`) must live
in the form itself instead of the file — a `ToolTip`/help text per field,
sourced from that same table. Once `config_mutagenmon.json` has been edited
at least once through this tab, it permanently becomes a comment-free JSON
file, even if hand-edited afterwards.

## Tab 4 — "Logs" (new, with missing infrastructure)

The biggest new piece of work: `FileLoggerProvider`
(`MutagenMon.App/FileLoggerProvider.cs`) only writes to file today (plus
Event Log for Critical), with no in-memory history at all. Needed:

- A ring buffer of the last 100 events (timestamp, level, category,
  message), fed on every `FileLoggerProvider.Write` call, exposed to the UI
  as an `ObservableCollection` updated on the UI thread (log entries arrive
  from background threads).
- **Clear**: empties only this buffer/grid, does not touch the file.
- **Open log file**: opens the resolved log file
  (`ResolveLogFilePath`) with the default application — that path is
  currently just a local variable inside `App.OnStartup`/`OnReloadReady`;
  it must become an instance field so the Logs tab can read it at any time
  (including after a reload that changes `LogPath`).
- **Clear log file**: truncates the file on disk — destructive, needs a
  confirmation dialog (`GenericMessageDialog.ShowConfirm`, same pattern as
  session deletion or Exit).

No file-reload button is needed — real-time only, via the in-memory
buffer, which is simpler than re-tailing a growing file.

## Phased plan

1. **Foundation**: `TabControl` in `StatusWindow`, Sync tab with its
   reduced top toolbar (Add + Stop), shared bottom toolbar (Reload config &
   restart + Exit mutagen monitor) — everything else unchanged.
2. **Mutagen Configuration tab**: extract `MutagenConfigEditorWindow` into
   an embedded `UserControl`, remove the modal window and its button.
3. **Mutagen monitor Configuration tab**: new `UserControl` with the
   structured form described above, Check/Save.
4. **Logs tab**: log ring buffer, grid `UserControl`, Clear / Open log file
   / Clear log file.

Each phase is independent and shippable on its own. No phase starts without
an explicit request — this plan is not auto-advanced.
