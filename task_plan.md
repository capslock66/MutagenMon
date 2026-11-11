# Task Plan: `@scriptSession` directive + "Run script" on the Sync tab

## Goal
A script tagged `# @scriptSession` appears in a combo on the Sync toolbar (right of Duplicate); the user picks one and runs it (button or F5) on the selected session, in a dialog showing the output and status. The Stop/Start button moves to the far right of the Sync toolbar.

## Next Step
Phase 5 (run dialog) is done. Wait for the user's go to start Phase 6.

## Current Phase
Phase 5 (done) — awaiting go before Phase 6

## Phases

### Phase 1: Analysis
- [x] 1.1 Read the script parser, `ScriptDefinition`, `ScriptFileEditor`, `ScriptRunner`
- [x] 1.2 Read the Scripts tab Run flow (`ScriptsView.xaml.cs`: confirm, macros, run, status, output pane)
- [x] 1.3 Read the Sync toolbar (`StatusWindow.xaml`) and the F5 handling (`OnWindowPreviewKeyDown`)
- [x] 1.4 Record findings in `findings.md`
- **Status:** complete

### Phase 2: Requirements & user tests (docs first)
- [x] 2.1 `requirements/11-scripts-tab-requirements.md`, FR-48 directive table: add the `@scriptSession` row: a boolean flag written without a value (`# @scriptSession`); the script is a "session script" (value true) when the line is present in it, and not one (value false) when it is absent. An explicit `: true` / `: false` is also accepted (D2)
- [x] 2.2 Same file: add FR-56 "Run a script on the selected session from the Sync tab" (combo content, Run button, F5, enabled rules, run dialog, single-run rule, refresh of the combo)
- [x] 2.3 Requirements for the Sync toolbar layout (find the Sync-tab/toolbar FR, FR-16) : Stop/Start button at the far right
- [x] 2.4 `requirements/UserTests.md`: add UT-56.x scenarios (tag parsing, combo content, disabled states, F5, dialog output/status, cancel, macro `{{session:…}}`, layout)
- **Status:** complete

### Phase 3: Core — parse the `@scriptSession` flag
- [x] 3.1 `ScriptDefinition`: add `bool IsSessionScript = false` (last optional parameter, so existing constructors keep compiling)
- [x] 3.2 `ScriptFileParser` (an `@scriptSession` value other than `true`/`false` makes the script invalid, FR-48.5): the current `DirectiveRegex` requires a colon, so `# @scriptSession` does not match. Add a dedicated flag regex (colon and value optional; key case-insensitive) and set the flag in `Build` (first occurrence wins, like the other directives)
- [x] 3.3 Confirm `ScriptFileEditor` (move/delete/add/rename) preserves the line untouched (it treats the body as opaque text); add no code unless a test shows otherwise
- [x] 3.4 Tests in `ScriptFileParserTests`: tag alone, tag with `: true` / `: false` (decision D2), tag absent, tag inside an otherwise invalid script, mixed case, tag after a non-directive line, duplicate tag
- [x] 3.5 Build and run `MutagenMon.Core` / `MutagenMon.Core.Tests` (per project, `-m:1`)
- **Status:** complete

### Phase 4: Shared run logic (refactor, no behaviour change)
- [x] 4.1 Extract from `ScriptsView` into a reusable App-level class (e.g. `ScriptRunCoordinator`): `DescribeConfirmation`, macro expansion (`TryExpandMacros`, `BuildMacroSources`, secrets dialog), `DescribeOutcome`, run + cancel via `ScriptRunner`
- [x] 4.2 The coordinator reports through callbacks/events (status text + error flag, output line) so both the Scripts tab and the new dialog can display them
- [x] 4.3 Rewire `ScriptsView.OnRunClick` / `OnCancelClick` on the coordinator; behaviour of the Scripts tab must stay identical (FR-51 to FR-55)
- [x] 4.4 Build, run all existing tests, manual re-check of UT-55.10 to UT-55.14 on the Scripts tab (done by the user on Windows: no regression)
- **Status:** complete

### Phase 5: Run dialog
- [x] 5.1 New `ScriptRunWindow` (XAML + code-behind): title with the script name and the session, read-only output pane (red for stderr, last `MaxOutputLines` lines, auto-scroll), status line, Cancel button (enabled while running), Close button (disabled while running)
- [x] 5.2 The dialog starts the run on open through the coordinator (`@confirm` and macros handled before the dialog opens, same as the Scripts tab)
- [x] 5.3 Closing the dialog (X, Esc, Close) while running is blocked (decision D4, FR-56.5): the user cancels first
- [x] 5.5 `ScriptRunCoordinator`: add a `RunningChanged` event (raised when IsRunning/IsCancelling change) so every tab refreshes its buttons (FR-56.6); the Scripts tab Cancel must stay disabled while a run started from the Sync dialog is in progress (review of phase 4)
- [x] 5.4 Log the user actions like the other windows (`User action: ...`), never the script output or expanded text (FR-54)
- **Status:** complete

### Phase 6: Sync tab UI
- [ ] 6.1 `StatusWindow.xaml`: right of Duplicate, add a `ComboBox` (scripts with `@scriptSession`, valid only, in file order, display name) and a `Run` button (icon like the Scripts tab, tooltip `Run (F5)`)
- [ ] 6.2 Move `ToggleMonitoringButton` (Stop/Start) to the far right of the toolbar (see Q1 for its position relative to "Resolve conflicts")
- [ ] 6.3 Enable rules: Run enabled only when a session is selected in the grid AND a script is selected in the combo AND no script run is in progress; same rule for F5
- [ ] 6.4 Combo content: read the scripts file via `scriptsPathProvider` + `ScriptFileParser`; refresh on window show, on config reload, and when the Scripts tab saves or reloads (new `ScriptsChanged` event on `ScriptsView`); keep the selection by name when the script still exists
- [ ] 6.5 Missing/unreadable scripts file: combo empty and disabled, no error popup (the Scripts tab already reports it)
- [ ] 6.6 `OnWindowPreviewKeyDown`: F5 on the Sync tab runs the selected script (does nothing when disabled); behaviour on the Scripts tab unchanged
- [ ] 6.7 Wire `OnRunSessionScriptClick`: the session = `GetSelectedSession()` (read once at click, FR-55.3), open `ScriptRunWindow` modal on the status window
- [ ] 6.8 Single-run guard shared with the Scripts tab (a run in progress on either tab disables Run on both)
- **Status:** pending

### Phase 7: Verification
- [ ] 7.1 Build `MutagenMon.Core`, `MutagenMon.Core.Tests`, `MutagenMon.App` individually (`-m:1`, WPF project: see the repo memory on per-project builds) and run the tests
- [ ] 7.2 Walk through UT-56.x (and UT-55.10 to UT-55.14 for regressions); mark them validated only after the user confirms (WPF cannot be exercised on this Linux host)
- [ ] 7.3 Review loop: launch `eortc-dotnet-code-reviewer` on the modified files (spec: `requirements/11-scripts-tab-requirements.md`), fix 🔴/🟠, repeat (max 3 iterations)
- **Status:** pending

### Phase 8: Delivery
- [ ] 8.1 Update `README.md` / `requirements/README.md` if they list directives or FR ranges
- [ ] 8.2 Summarise to the user: changes, review iterations, remaining minor findings, manual scenarios to run on Windows
- [ ] 8.3 No commit unless asked (via `/eortc-git-push`, format without body or trailer, see `CLAUDE.md`)
- **Status:** pending

## Open Questions
- **Q1** (answered by the user): the "Stop Mutagen sessions" button moves to the far right of the Sync toolbar. Layout: left = Add, Duplicate, script combo, Run; right = Resolve conflicts, then Stop/Start at the very right.

## Decisions Made
| ID | Decision | Rationale |
|----|----------|-----------|
| D1 | The flag has its own regex; the generic `@key: value` regex is untouched | It requires a colon; changing it could alter how existing directives parse |
| D2 | `# @scriptSession` alone = true; `: true` / `: false` also accepted | Plain tag is the requested syntax; accepting an explicit value avoids a silent misread of `@scriptSession: false` |
| D3 | Combo lists valid scripts only (valid = has `@host`, no duplicate name, etc.) | An invalid script cannot run (FR-49.2) |
| D4 | Close is blocked while the script runs; the user must Cancel first (proposed) | Avoids an orphaned ssh process; same Cancel semantics as FR-51.5 |
| D5 | Sync tab runs the saved version of the script (file on disk), not unsaved edits of the Scripts tab | The Sync tab has no editor; the Scripts tab Run auto-saves, the Sync tab does not touch the file |
| D6 | Run logic extracted into a shared class rather than duplicated | CLAUDE.md memory: no duplicated logic; both entry points must behave identically |
| D8 | After phase 4, the rename/delete blocking of the Scripts tab while any script runs stays as is | Safe; revisit only if it bothers in practice (review of phase 4) |
| D9 | Cancel is offered only on the running script's own view (user decision after testing); fallback when that script left the list | A run must always stay cancellable (review of phase 4) |
| D7 | The project works on `main` (no `claude/*` branch) | Repo memory: mutagenMon ignores the branch rules |

## Errors Encountered
| Error | Resolution |
|-------|------------|
