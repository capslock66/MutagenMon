# Progress Log

## Session: 2026-11-07 / 2026-11-08

### Current Status
- **Phase:** 3 complete, Phase 4 not started (waiting for the user's request)
- **Started:** 2026-11-07

### Actions Taken
- Designed the macro feature in `plan-script-macros.md` (env, session, reg, cred, secret; decisions 1-18).
- Created `task_plan.md`, `findings.md`, `progress.md` from that plan and numbered the tasks.
- Phase 2 (documentation, committed): FR-53.2/53.3, FR-55.1 to FR-55.11 and UT-55.11 to UT-55.14 written.
- Phase 3: `ScriptMacro` (parser), `ScriptMacroExpander` (+ `ScriptMacroSources`, `ScriptMacroExpansion`), `ScriptOutputMasker`, `Ssh/ShellQuoting` (extracted from `ConflictFileClient`), and tests (`ScriptMacroTests`, `ScriptMacroExpanderTests`, `ScriptOutputMaskerTests`, `ShellQuotingTests`; the `ShellQuote` theory moved from `ConflictFileClientTests`).
- Phase 3 spec follow-ups in `11-scripts-tab-requirements.md`: session property name and registry hive matched ignoring case (FR-55.3, FR-55.9), masking of the escaped form of a value containing `'` and short-secret note (FR-55.8), Implementation section lists the new classes and tests.
- Review loop (`eortc-dotnet-code-reviewer`): iteration 1 no 🔴/🟠 (5 minor fixed); iteration 2 one 🟠 (secret with `'` not masked under `set -x`) fixed with 4 minor; iteration 3 one 🟠 (a value with `'` inside a longer word, traced as `'-ppa'\''ss'`, was still not masked). Fixed afterwards by masking the escaped form without outer quotes (`ShellQuoting.EscapeSingleQuotes`), confirmed against real `bash -x`. This last fix was reviewed in the pre-commit review (a new loop): approved, no 🔴/🟠 left. Optional minors not done: a direct `[Theory]` for `EscapeSingleQuotes`, tests for a secret starting or ending with `'`, a very long regex comment line in `ScriptMacro.cs`.

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| `dotnet test MutagenMon.Core.Tests -m:1` | all pass | 386 passed, 0 failed | pass |

### Errors
| Error | Resolution |
|-------|------------|
| Several edits reported as applied were not on disk (`plan-script-macros.md`, `11-scripts-tab-requirements.md`, `ScriptMacroExpanderTests.cs`) | Detected by grep or a failing test, re-applied, verified |
