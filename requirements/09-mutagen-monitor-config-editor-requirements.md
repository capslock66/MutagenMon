# Mutagen Monitor Configuration Editor — Requirements & Design

This document specifies a new capability: viewing and editing MutagenMon's
own configuration file, `config_mutagenmon.json`, as a structured form
directly from the status window, instead of requiring the user to hand-edit
it and restart.

**Status: implemented.** Core: `MutagenMon.Core/Configuration/ConfigLoader.cs`
(load/parse/serialize/save — extracted from what used to be private methods
in `App.xaml.cs`, so `App`'s own startup load and this tab's Check/Save
share one implementation). App:
`MutagenMon.App/MutagenMonitorConfigEditorView.xaml`/`.xaml.cs`, hosted as a
tab in `StatusWindow.xaml`. Unit-tested in `MutagenMon.Core.Tests`
(`ConfigLoaderTests`).

Part of the tabbed status-view rewrite tracked in `TABS_UI_PLAN.md` at the
repository root — see
[07-session-management-requirements.md](07-session-management-requirements.md)'s
FR-16 revision note for the shared bottom toolbar this tab's "Reload config
& restart" comes from.

## Relationship to existing requirements

- This is a **different file** from the one
  [08-mutagen-config-editor-requirements.md](08-mutagen-config-editor-requirements.md)
  edits: `config_mutagenmon.json` is MutagenMon's own configuration
  (documented in
  [06-configuration-reference.md](06-configuration-reference.md)), fully
  owned by this application — unlike `~/.mutagen.yml`, whose structure
  belongs to `mutagen` itself.
- Continues the FR numbering from 08-...md's FR-33, as FR-34 through FR-38.
- Because this file's schema is fully owned and typed
  (`MutagenMonOptions`), this editor is a **structured form** (one field
  per key), unlike FR-30's raw-text editor for `~/.mutagen.yml` (whose
  structure MutagenMon doesn't own).

## FR-34 — Tab

- FR-34.1: `StatusWindow` gains a third tab, **"Mutagen monitor
  Configuration"**, alongside "Sync" and "Mutagen Configuration".
- FR-34.2: The tab has its own top toolbar with **Check** and **Save**
  only — "Reload config & restart" is not duplicated here; it lives once,
  in the toolbar shared by every tab (FR-38).

## FR-35 — Load

- FR-35.1: `config_mutagenmon.json` is guaranteed to already exist and
  parse by the time this tab can be shown at all — `App.OnStartup` fails to
  start the whole application otherwise. Unlike FR-30.4's handling of a
  possibly-missing `~/.mutagen.yml`, this tab has no "file doesn't exist
  yet" state to handle.
- FR-35.2: The file is read once, when the tab's hosting control
  (`MutagenMonitorConfigEditorView`) is constructed — i.e. the first time
  the status window is shown, not on every tab selection. Same caveat as
  08-...md's tab-hosting revision note for `MutagenConfigEditorView`: an
  external edit made afterwards isn't picked up until the app restarts.
- FR-35.3: Every key in 06-configuration-reference.md gets its own typed
  form field — text box, checkbox, combo box, or (for `StatusMaxLag`) a
  group of four numeric fields — **except `DebugLevel`** (legacy,
  documented as having no effect), which has no field and is carried
  through from the loaded file unchanged on every Save.
- FR-35.4: Each field's label carries a `ToolTip` with that key's
  description, sourced from 06-configuration-reference.md's table — see
  FR-37.2 for why this replaces the shipped file's `#` comments as the
  place that documentation lives once this tab has saved the file.

## FR-36 — Validation ("Check")

- FR-36.1: **"Check"** validates every field without writing to disk:
  - Every numeric field (poll period, session thresholds, profile grace,
    auto-resolve history age, the four `StatusMaxLag` fields) must parse as
    a whole number.
  - Each `AutoResolve` rule's `filepath` must be a syntactically valid
    .NET regular expression (mirrors FR-31.4's "syntax only, not semantic"
    scope for the other editor's YAML validation — mutagen/`.NET`'s own
    regex engine is the authority here, not this form).
  - Each rule's `Resolve` combo is restricted to `"A wins"`/`"B wins"` at
    the UI level, so it cannot itself be invalid.
  - Each `SshServers` entry's `Host` must be non-empty and unique
    (case-insensitive) among the other entries — a blank or duplicate host
    would make it ambiguous or useless as a picklist entry in the Add/Edit
    session window's "Browse SSH server…" flow (07-...md's FR-18.5).
- FR-36.2: Errors are shown in a red text area below the form — same
  convention as FR-31.1.
- FR-36.3: **Save also runs this same validation first** (mirrors FR-31.3)
  — blocked on any error, showing the same red area, rather than writing a
  file the application would then fail to reload.

## FR-37 — Save

- FR-37.1: On valid content, `ConfigLoader.Save` serializes the form's
  current values — plus the untouched `DebugLevel` (FR-35.3) — to
  `config_mutagenmon.json` as plain, indented JSON.
- FR-37.2: **(confirmed)** A structured form serializing clean JSON has no
  portable way to preserve the shipped file's `#`-prefixed whole-line
  comments (JSON has no native comment syntax, and reinventing one on
  write would just add a second non-standard convention). **Once
  `config_mutagenmon.json` has been saved through this tab even once, it
  permanently loses those comments** — even if later hand-edited again.
  This is an accepted tradeoff, not a bug (see `TABS_UI_PLAN.md`'s
  "Resolved: comment loss on save"); FR-35.4's per-field tooltips exist
  specifically to not lose the documentation those comments carried.
- FR-37.3: After a successful save, a note is shown (mirrors FR-32.4) that
  the change does not affect the already-running monitor — "Reload config
  & restart" (FR-38) applies it.

## FR-38 — Reload config & restart

- FR-38.1: This tab has **no reload button of its own** — like 08-...md's
  FR-33 revision for `MutagenConfigEditorView`, it relies entirely on the
  toolbar shared by every tab, below the `TabControl` (07-...md's FR-16
  revision note), calling the same `ReloadConfig()` (FR-7.1) as every other
  tab.

## Auto-resolve rule editor design

`AutoResolve` (an ordered list, first-match-wins per
06-configuration-reference.md) is edited as a `DataGrid` bound directly to
`AutoResolveRule` (the same Core model `MutagenMonOptions.AutoResolve`
already uses — no separate UI row type): an editable "Filepath (regex)"
text column, a "Resolve" combo column limited to `"A wins"`/`"B wins"`, and
per-row **Up**/**Down**/**Remove** buttons plus a **"+ Add rule"** button
below the grid, since the rule order is significant and must stay
user-controllable.

## SSH servers editor design

`SshServers` (an unordered list — no first-match semantics, unlike
`AutoResolve`) is edited as a `DataGrid` bound directly to
`SshServerEntry` (the same Core model `MutagenMonOptions.SshServers`
already uses): a single editable "Host" text column, plus a per-row
**Remove** button and a **"+ Add server"** button below the grid — no
**Up**/**Down** buttons, since entry order has no effect on the Add/Edit
session window's "Browse SSH server…" picklist (07-...md's FR-18.5).

## Implementation

- `MutagenMon.Core/Configuration/ConfigLoader.cs`: `Load(path)`,
  `Parse(rawText)` (comment-stripping + deserialize + `%USERPROFILE%`
  expansion for `MutagenProfileDir`), `Serialize(options)`, and
  `Save(path, options)`. Extracted from `App.xaml.cs`'s private
  `LoadConfig`/`ParseConfigText`/`StripConfigCommentLines` — behavior
  unchanged, now shared instead of duplicated, and unit-testable (it
  wasn't, as private App-project methods). `App.LoadConfig` is now a
  one-line wrapper kept only so its existing call sites read the same.
- `MutagenMon.App/MutagenMonitorConfigEditorView.xaml`/`.xaml.cs`: the
  form itself, laid out as a master/detail `TabControl` with
  `TabStripPlacement="Left"` — one tab per section (General, SSH servers,
  Logging & notifications, Paths, Session thresholds & polling, Status max
  lag, Auto-resolve rules), each tab's content a `ScrollViewer` around a
  `Label`+control-per-row `StackPanel`, following `SessionEditWindow`'s
  established structured-form field style. (Originally a single
  top-to-bottom `ScrollViewer` of stacked `GroupBox` zones; switched to
  this left-hand section list once the number of sections grew unwieldy to
  scroll through.) Hosted directly in `StatusWindow.xaml` as the "Mutagen
  monitor Configuration" `TabItem`; `StatusWindow`'s constructor calls
  `Initialize(logger)` on it once, same pattern as
  `MutagenConfigEditorView`.
