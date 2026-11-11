# Progress Log

## Session 2026-11-11
- Phase 1 (analysis) complete: parser, Scripts tab run flow, Sync toolbar and F5 handling read; findings recorded.
- Plan written in `task_plan.md` (8 phases, numbered tasks). No code modified yet.
- Q1 answered: Stop/Start button goes to the far right of the Sync toolbar.
- Waiting for the user's go to start Phase 2.

## Phase 2 (requirements and user tests)
- `requirements/11-scripts-tab-requirements.md`: `@scriptSession` row and directive-line note in FR-48.5; new FR-56 (56.1 to 56.7); header note.
- `requirements/07-session-management-requirements.md`: planned-revision note on FR-16.5 (toggle moves to the far right).
- `requirements/README.md`: FR-56 listed as planned.
- `requirements/UserTests.md`: UT-56.1 to UT-56.6 (marked pending).
- Docs only: no code review required. Waiting for the user's validation before Phase 3.

## Phase 3 (core parsing)
- `ScriptDefinition.IsSessionScript` (last optional parameter); `ScriptFileParser`: dedicated `SessionFlagRegex` checked before the generic directive regex; first occurrence wins; value other than true/false makes the script invalid.
- `ScriptFileEditor` needed no change: two tests added (move and rename keep the `# @scriptSession` line).
- Tests: 17 new cases in `ScriptFileParserTests`, 2 in `ScriptFileEditorTests`. Core test suite: 475 passed, 4 skipped (Windows-only), 0 failed.
- Review loop: 1 iteration, no blocking or important finding. Minor: `# @scriptSession true` (no colon) is a plain comment and `# @scriptSession:` means true; both now documented in FR-48.5.
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
