# Findings

## Parser (`MutagenMon.Core/Scripts/ScriptFileParser.cs`)
- `DirectiveRegex` = `^#\s*@([A-Za-z\-]+)\s*:\s*(.*)$`: the colon is mandatory, so `# @session-script` is not matched today. A dedicated flag regex is needed.
- `Build` lowercases the key, first occurrence wins, unknown keys are ignored.
- `ScriptDefinition` is a record with optional trailing `Group = ""`; adding another optional parameter after it is source-compatible.
- `ScriptFileEditor` handles sections as opaque lines (only `ReadGroup` inspects a directive): the new tag should survive move/delete/rename untouched.

## Scripts tab Run flow (`MutagenMon.App/ScriptsView.xaml.cs`)
- `OnRunClick`: save drafts (FR-51.1) → read selected session once (FR-55.3) → `@confirm` dialog → macro expansion (secrets dialog) → `ScriptRunner.RunAsync(script, onOutput, token, expansion)` → `DescribeOutcome`.
- Output is cached per script (`ScriptRunView`, last `MaxOutputLines`), displayed in a `RichTextBox`; stderr in red.
- `_selectedSessionProvider` already returns the session selected in the Sync grid.
- `RunSelectedScript()` is called by F5 from `StatusWindow.OnWindowPreviewKeyDown`, only when the Scripts tab is selected.

## Sync tab (`MutagenMon.App/StatusWindow.xaml`)
- Toolbar: left `StackPanel` = Add, Duplicate, `ToggleMonitoringButton` ("Stop Mutagen sessions", width 150); right = `ResolveConflictsButton`.
- `DuplicateSessionButton` enabled by `OnSessionsGridSelectionChanged` when a `SessionSummaryRow` is selected; `GetSelectedSession()` gives the row.
- `StatusWindow` receives `scriptsPathProvider` and `scriptRunnerFactory` already (see `Initialize`).

## Docs
- Scripts requirements: `requirements/11-scripts-tab-requirements.md` (FR-47 to FR-55; directive table in FR-48, `@group` row at line 96). Next free number: FR-56.
- User tests: `requirements/UserTests.md`, last block UT-55.14. Repo memory: every implementation request must also update it.

## Risks
- WPF cannot run on this Linux host: UI verification is manual on Windows.
- Refactor in phase 4 touches the Scripts tab Run path (FR-51 to FR-55): regress-test UT-55.10 to UT-55.14.

## Open point for Windows testing (phase 5 review)
- Exit from the tray menu while the Sync-tab run dialog is running: `ScriptRunWindow.OnClosing` cancels the close while the script runs; check on Windows whether the shutdown is blocked or the dialog closes, and that the ssh process of the run is then cancelled or killed (FR-56.5 does not cover it).
