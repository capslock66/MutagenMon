# Progress Log

## Session 2026-11-11
- Phase 1 (analysis) complete: parser, Scripts tab run flow, Sync toolbar and F5 handling read; findings recorded.
- Plan written in `task_plan.md` (8 phases, numbered tasks). No code modified yet.
- Q1 answered: Stop/Start button goes to the far right of the Sync toolbar.
- Waiting for the user's go to start Phase 2.

## Phase 2 (requirements and user tests)
- `requirements/11-scripts-tab-requirements.md`: `@session-script` row and directive-line note in FR-48.5; new FR-56 (56.1 to 56.7); header note.
- `requirements/07-session-management-requirements.md`: planned-revision note on FR-16.5 (toggle moves to the far right).
- `requirements/README.md`: FR-56 listed as planned.
- `requirements/UserTests.md`: UT-56.1 to UT-56.6 (marked pending).
- Docs only: no code review required. Waiting for the user's validation before Phase 3.

## Phase 3 (core parsing)
- `ScriptDefinition.IsSessionScript` (last optional parameter); `ScriptFileParser`: dedicated `SessionFlagRegex` checked before the generic directive regex; first occurrence wins; value other than true/false makes the script invalid.
- `ScriptFileEditor` needed no change: two tests added (move and rename keep the `# @session-script` line).
- Tests: 17 new cases in `ScriptFileParserTests`, 2 in `ScriptFileEditorTests`. Core test suite: 475 passed, 4 skipped (Windows-only), 0 failed.
- Review loop: 1 iteration, no blocking or important finding. Minor: `# @session-script true` (no colon) is a plain comment and `# @session-script:` means true; both now documented in FR-48.5.
- Not committed.

## Phase 4 (shared run logic)
- New `MutagenMon.App/ScriptRunCoordinator.cs`: confirmation, macro expansion, ScriptRunner call, cancel, outcome text, `IsRunning`/`IsCancelling`; reports through `ScriptRunCallbacks` (NotStarted, Starting, Status, Output, Finished).
- `ScriptsView` rewired (about 170 lines removed); `StatusWindow` creates the single coordinator and passes it to the Scripts tab.
- Build `MutagenMon.App`: 0 warning, 0 error. Core tests: 475 passed, 4 skipped, 0 failed.
- Review loop: 1 iteration, no blocking or important finding. Fixed: inaccurate comment on exceptions; `Starting`/`Status(Running)` moved inside the try/finally so `IsRunning` cannot stay stuck. Deferred to phase 5: `RunningChanged` event (task 5.5).
- Manual non-regression (UT-55.10 to UT-55.14) to be done on Windows. Not committed.

## Cancel behaviour change (user decision after testing phase 4)
- Cancel is enabled only while the displayed script is the running one (FR-47.2 updated): `ScriptsView._runningScriptName`; fallback when the running script is no longer in the list; disabled for a run started elsewhere (FR-56.6).
- Docs: FR-47.2, UT-51.2b (new), comment in `ScriptsView.xaml`.
- Review loop: 2 iterations. Iteration 1: one important finding (Cancel unreachable if the running script left the list), fixed with the fallback. Iteration 2: no blocking or important finding; minor points fixed. Build 0 warning, 0 error. Not committed.

## Elapsed time while a script runs (user request after testing phase 4)
- `ScriptRunCoordinator`: `RunInfoChanged` event, `StartTicker`/`StopTicker` (stopwatch-based, `DispatcherTimer` at 250 ms, publishes on each new second; "Cancelling..." is kept in the status line); `StatusWindow` appends the text to its title.
- Docs: FR-52.3 updated, FR-52.6 new, UT-52.2 new (pending).
- Review loop: 1 iteration, no blocking or important finding. Minor fixes applied: timer handler protected (log and stop), wording of FR-52.6 and of the comment, FR-52.6 moved after FR-52.5. Build 0 warning, 0 error. Not committed.
- Untracked build leftover `MutagenMon.App/MutagenMon.App_*_wpftmp.csproj` must not be committed.

- UT-52.2 (elapsed time in the status line and the title) validated by the user on Windows.
- UT-51.2b (Cancel only on the running script) validated by the user on Windows.
- UT-55.10 to UT-55.14 re-checked by the user on Windows after the phase 4 refactor: no regression.

## Phase 5 (run dialog)
- `ScriptRunCoordinator`: `Prepare` (confirmation + macros, returns `ScriptRunPreparation`), `RunAsync` = Prepare + `RunPreparedAsync`, `RunningChanged` event; `ScriptsView` subscribes to it.
- New `ScriptRunWindow` (modal; output, status, Cancel while running, Close once ended; closing blocked while running; message shown when the run cannot start) and `ScriptOutputPane` (shared append helper). Not reachable from the UI yet (phase 6).
- Review loop: no blocking or important finding. Minor fixes: `ScriptMacroExpansion.ToString` no longer prints the expanded text or the secrets (new Core test), refusal reported when a run is already in progress, close logged in `OnClosed`, `RunningChanged` raised last in `Cancel`, `MemberNotNullWhen`, obsolete doc bullet, unused using. Build 0 warning, 0 error; Core tests 476 passed, 4 skipped.

## Phase 6 (Sync tab UI)
- `StatusWindow.xaml`: toolbar in two columns; left = Add, Duplicate, session-script combo, Run (green triangle, tooltip "Run (F5)"); right = Resolve conflicts, then Stop/Start Mutagen sessions as the rightmost control. `SyncTab` named.
- `StatusWindow.xaml.cs`: `RefreshSessionScripts` (valid `@session-script` scripts read from the file; empty and disabled combo when none or unreadable; selection kept by name), rebuilt on `ScriptsView.ScriptsChanged`, window `Activated`, end of a config reload; `UpdateRunSessionScriptButton` (session selected + script selected + no run, refreshed on grid selection, `UpdateContent`, `RunningChanged`); `OnRunSessionScriptClick` (file re-read, session read once, `Prepare`, `ScriptRunWindow.ShowRun`); F5 on the Sync tab.
- `ScriptsView`: `ScriptsChanged` event raised after every load of the scripts file.
- Docs: FR-56 and README marked implemented (UT-56.x pending), FR-16.5 note.
- Build 0 warning, 0 error. Review pending at the time of writing. Not committed.
- Phase 6 review, iteration 1: two important findings fixed. (1) The end of a config reload was detected by an edge on `reloadInProgress` that is not always published (sessions already stopped): replaced by an explicit `RefreshSessionScripts()` call at the end of `App.OnReloadReady`. (2) Toolbar clipped near the 640 px minimum width: `MinWidth` of the status window raised to 780. Minor: summary of `LoadScripts` put back, F5 and click share `RunSessionScript`, the click is logged before the file is re-read. Second review pending.
- Phase 6 review, iteration 2: no blocking or important finding. Minor fixes: UT-51/52 text updated to the new minimum width (780), UT-56.2 gets the external-edit scenario, summaries and the `ScriptsChanged` declaration moved. Build 0 warning, 0 error. Not committed.
- Directive renamed from @scriptSession to @session-script (user decision); review: one important finding (duplicate InlineData, xUnit1025) fixed. Core tests 477 passed, 4 skipped; build 0 warning.

- UT-56.1 to UT-56.6 validated by the user on Windows; marked done in UserTests.md and the requirements docs.
- 7.1 done: Core tests run on Windows by the user: 481 total, 0 failed, 469 passed, 12 skipped (platform-conditional); Linux run: 477 passed, 4 skipped.
- 7.3 done: review iteration 1 (eortc-dotnet-code-reviewer, whole feature): no blocking or important finding. Fixed: ScriptDefinition field list in the spec, lambda parameter name in ScriptRunCoordinator. Left open: no message when the chosen script is no longer a valid session script on disk (F5/Run silently does nothing), Cancel Task.Run only catches ObjectDisposedException, no automated test for ScriptRunCoordinator (WPF project), Exit-from-tray and tray windows during a Sync run to test on Windows.
- 8 done: root README.md lists no directives or FR ranges (no change); requirements/README.md already up to date (FR-56 implemented, UT-56.x validated). Summary given to the user. Not committed.
