# Logs Tab — Requirements & Design

This document specifies a new capability: a real-time view of the
application's own recent log entries, directly from the status window,
instead of requiring the user to open `mutagenMon.log` in an external text
editor/tail tool.

**Status: implemented.** App:
`MutagenMon.App/FileLoggerProvider.cs` (in-memory ring buffer + live
`EntryLogged` event, on top of its existing file/Event Log sinks),
`MutagenMon.App/LogEntry.cs` (the record shown), `LogsView.xaml`/`.xaml.cs`
(the tab itself), wired into `StatusWindow.xaml`/`.xaml.cs` and
`App.xaml.cs`.

Part of the tabbed status-view rewrite tracked in `TABS_UI_PLAN.md` at the
repository root — see
[07-session-management-requirements.md](07-session-management-requirements.md)'s
FR-16 revision note for the shared bottom toolbar, and
[09-mutagen-monitor-config-editor-requirements.md](09-mutagen-monitor-config-editor-requirements.md)
for the tab immediately before this one. Continues the FR numbering from
09-...md's FR-38, as FR-39 through FR-42.

## FR-39 — Tab

- FR-39.1: `StatusWindow` gains a fourth tab, **"Logs"**, alongside "Sync",
  "Mutagen Configuration", and "Mutagen monitor Configuration".
- FR-39.2: The tab's own top toolbar has three actions: **Clear**, **Open
  log file**, **Clear log file** — no "Reload config & restart" here; this
  tab has nothing to reload (unlike the other two config-editing tabs).

## FR-40 — Real-time event grid

- FR-40.1: The tab shows a grid of the last **100** logged events —
  Timestamp, Level, Category, Message columns — matching every entry
  `FileLoggerProvider` writes to the primary log file and/or the Windows
  Event Log (FR-14), not a separate/filtered stream.
- FR-40.2: **No file re-read/reload**: the grid is populated once, when the
  tab's hosting control (`LogsView`) is constructed, from
  `FileLoggerProvider.GetRecentEntries()` — a 100-entry in-memory ring
  buffer `FileLoggerProvider` now keeps regardless of whether any Logs tab
  is open, so the grid isn't empty just because the tab was opened after
  those entries were logged. From then on it updates live via
  `FileLoggerProvider.EntryLogged`, never by re-reading the log file.
- FR-40.3: Log entries are produced on arbitrary background threads (most
  logging in this app happens from the poller, not the UI thread) — every
  update to the grid's bound collection is marshaled to the UI thread
  (`Dispatcher.BeginInvoke`).
- FR-40.4: The grid trims to the same 100-row cap independently on its own
  side, so it self-limits even across a "Clear" (FR-41) — see there.

## FR-41 — Clear (the grid only)

- FR-41.1: **"Clear"** empties this tab's own displayed collection only.
  It does NOT touch the log file on disk, the Windows Event Log, or
  `FileLoggerProvider`'s own 100-entry backlog (FR-40.2) — a later reopen
  of the tab within the same app run would still show the pre-Clear
  backlog if the control were reconstructed, though in practice it never
  is (the tab, like every other, is hosted once for the status window's
  lifetime — see FR-40.2's revision note pattern already established for
  the other two editor tabs).
- FR-41.2: After Clear, new entries keep arriving in real time exactly as
  before (FR-40.3) — Clear does not pause or detach the live feed.

## FR-42 — Open log file / Clear log file

- FR-42.1: **"Open log file"** launches `FileLoggerProvider.PrimaryLogPath`
  with the OS's default handler for that file type (`Process.Start` with
  `UseShellExecute = true`) — the same path the app itself is currently
  writing to, read live rather than cached, since a config reload
  (FR-7.1) can change `LogPath` mid-session. If the path is unknown yet or
  the file doesn't exist on disk yet, an informational dialog is shown
  instead of attempting to launch it.
- FR-42.2: **"Clear log file"** truncates that same file on disk
  (`File.WriteAllText(path, "")`) — destructive and irreversible, so it
  requires a Yes/No confirmation first (`GenericMessageDialog.ShowConfirm`,
  same pattern as session deletion or Exit), naming the exact path being
  cleared. Safe to do at any time regardless of what else the app is
  doing: `FileLoggerProvider` opens, appends, and closes the file on every
  single write with no persistent handle held between writes (see its own
  remarks), so there's nothing to flush/reopen/coordinate around a
  concurrent truncate.
- FR-42.3: Neither action touches the Windows Event Log or
  `FileLoggerProvider`'s in-memory backlog (FR-40.2) — only the on-disk
  primary file.

## FR-43 — Master/detail split

- FR-43.1: The grid (FR-40) is the master; a resizable detail panel below
  it shows the currently-selected row's full `Message` (which already
  includes any appended exception text — see `FileLoggerProvider`'s
  `FormatMessage`), word-wrapped.
- FR-43.2: A horizontal `GridSplitter` separates the two, so the user can
  resize either pane. The detail panel is **2 lines tall by default**
  (roughly 40px) and scrollable (`VerticalScrollBarVisibility="Auto"`) —
  useful for a message/exception that's taller than the default height
  without resizing the splitter every time.
- FR-43.3: Selecting nothing (or a row that no longer exists, e.g. after
  "Clear", FR-41) leaves the detail panel empty.
- FR-43.4: The grid's own Message column never shows more than one line:
  `LogEntry.GridSummary` collapses `Message` to its first line, appending
  `" [+N more line(s)]"` when there were more — a multi-line message (e.g.
  one with an appended exception) would otherwise blow out that row's
  height in the grid. The uncollapsed `Message` is still what the detail
  panel (FR-43.1) shows for the selected row.

## FR-44 — Error and Critical rows highlighted in red

- FR-44.1: Grid rows whose `Level` is `Error` or `Critical` are rendered
  with red foreground text (`DataGridRow.Foreground`), via a `DataTrigger`
  bound to `LogEntry.IsErrorOrCritical`. `Warning`/other levels are
  unaffected.

## FR-45 — "Generate exception" button (test aid)

- FR-45.1: The tab's toolbar gains a fourth action, **"Generate
  exception"**, hidden by default (`Visibility="Collapsed"`).
- FR-45.2: Visible only when `ShowGenerateException` (see
  [06-configuration-reference.md](06-configuration-reference.md)) is
  `true` in `config_mutagenmon.json` — default `false`. Also exposed as a
  checkbox ("Show 'Generate exception' button") in the "Mutagen monitor
  Configuration" tab's "Logging & notifications" group
  (`MutagenMonitorConfigEditorView`).
- FR-45.3: Clicking it throws a deliberate, unhandled `InvalidOperationException`
  on the UI thread — the same repro the legacy "Boum" test button gave
  (see `UserTests.md`'s UT-14.1 rewrite note), reusing the FR-14.1 path
  end to end: `App.OnDispatcherUnhandledException` catches it, shows the
  "MutagenMon — error" dialog, and logs a Critical entry (reaching both
  `mutagenMon.log` and the Windows Event Log).
- FR-45.4: The flag is read once, at `StatusWindow` construction
  (`App.ShowStatusWindow`) — consistent with the other two config-editing
  tabs, which likewise read their values once rather than reacting live to
  a config reload (FR-7.1).

## FR-46 — Auto-scroll ("sticky bottom")

- FR-46.1: As new entries arrive (FR-40.3), the grid automatically scrolls
  so the newest row stays visible — the user never has to manually scroll
  down to see the latest line.
- FR-46.2: Selecting a row (FR-43.1) suspends auto-scroll: new entries
  keep arriving in the grid (FR-41.2-style — nothing pauses the feed
  itself) but the view no longer jumps to the bottom, so the row the user
  is reading stays in place and in view.
- FR-46.3: Auto-scroll resumes the moment the user manually scrolls the
  grid back down to its bottom — no separate control for it; scrolling
  away from the bottom (regardless of whether a row is selected) also
  suspends it, the same as FR-46.2.
- FR-46.4: "Clear" (FR-41) always resets auto-scroll back on, since the
  grid it would apply to is now empty.

## Implementation

- `MutagenMon.Core/Configuration/MutagenMonOptions.cs`: added
  `ShowGenerateException` (bool, default `false`).
- `MutagenMon.App/FileLoggerProvider.cs`: added a `Queue<LogEntry>` capped
  at 100 (`MaxRecentEntries`), populated inside the existing `Write`
  method under the same `_writeLock` used for the file/Event Log sinks;
  `GetRecentEntries()` returns a snapshot copy. Added `EntryLogged`
  (fired outside the lock, so a slow subscriber can't block logging) and a
  `PrimaryLogPath` property (same lock, mirrors the existing
  `SetPrimaryLogPath` setter) so `LogsView` can read the live path instead
  of caching a copy that a reload could invalidate.
- `MutagenMon.App/LogEntry.cs`: the record shown (`Timestamp`, `Level`,
  `Category`, `Message`) — distinct from `FileLoggerProvider`'s own
  free-text file-line formatting. Added the computed `IsErrorOrCritical`
  (FR-44) and `GridSummary` (FR-43.4) properties.
- `MutagenMon.App/LogsView.xaml`/`.xaml.cs`: the tab itself, hosted
  directly in `StatusWindow.xaml`. `StatusWindow`'s constructor now also
  takes the app's single `FileLoggerProvider` instance (alongside the
  logger/icon cache it already took) and a `showGenerateException` flag,
  and calls `Initialize(logger, loggerProvider, showGenerateException)` on
  this tab once, same pattern as the other two editor tabs. Master/detail
  split, red error rows, and the gated "Generate exception" button (FR-43
  through FR-45) all live here. Auto-scroll (FR-46) is a `_autoScroll`
  flag: new entries call `ScrollIntoView` only while it's true; selecting
  a row, or scrolling away from the bottom, clears it; scrolling back to
  the bottom (detected via the grid's internal `ScrollViewer.ScrollChanged`,
  found once via a `VisualTreeHelper` walk) sets it again. A
  `_suppressScrollChanged` flag (cleared on a later dispatcher pass, since
  the resulting layout/`ScrollChanged` can land after `ScrollIntoView`
  returns) stops our own programmatic scroll from being misread as the
  user scrolling away.
- `MutagenMon.App/MutagenMonitorConfigEditorView.xaml`/`.xaml.cs`: added
  the "Show 'Generate exception' button" checkbox (FR-45.2).
