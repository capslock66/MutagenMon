# Progress Log

## Session: 2026-11-07 / 2026-11-08

### Current Status
- **Phase:** 4 complete, Phase 5 not started (waiting for the user's request)
- **Started:** 2026-11-07

### Actions Taken
- Designed the macro feature in `plan-script-macros.md` (env, session, reg, cred, secret; decisions 1-18).
- Created `task_plan.md`, `findings.md`, `progress.md` from that plan and numbered the tasks.
- Phase 2 (documentation, committed): FR-53.2/53.3, FR-55.1 to FR-55.11 and UT-55.11 to UT-55.14 written.
- Phase 3 (committed, `c9c1869`): `ScriptMacro`, `ScriptMacroExpander` (+ `ScriptMacroSources`, `ScriptMacroExpansion`), `ScriptOutputMasker`, `Ssh/ShellQuoting`, and their tests; review loop of 3 iterations plus a pre-commit review that approved the last fix.
- Phase 4 (committed, `09f9ab6`): `MutagenMon.Core/Scripts/WindowsMacroValueReader.cs` with `ReadEnvironmentVariable` (HKCU then HKLM then process environment, `%NAME%` expansion with cycle guard and depth limit 5), `ReadRegistryValue` (HKCU/HKLM, 64-bit view, REG_SZ/EXPAND_SZ/DWORD/QWORD), `ReadCredentialPassword` (`CredReadW`, generic credential, strict UTF-16 decoding by the pure `DecodeCredentialBlob`); all return `null` instead of throwing, and `null` off Windows (env: process environment only).
- Phase 4 tests: `WindowsMacroValueReaderTests` (pure formatting, expansion, chain resolution, blob decoding, off-Windows behaviour, and 4 Windows-only tests that skip themselves elsewhere).
- Phase 4 spec: FR-55.2 expansion rules, FR-55.3 session property table (task 4.3), FR-55.9 and FR-55.10 wording, Implementation section, UT-55.11 to UT-55.13 extended.
- Review loop on Phase 4 (`eortc-dotnet-code-reviewer`): iteration 1 no 🔴/🟠 (cycle guard, `%` scan, blob size, doc and tests fixed); iteration 2 no 🔴/🟠 (blob decoding extracted as a pure function, spec and UserTests aligned); iteration 3 no 🔴/🟠. The minors of iteration 3 (one `try` per registry hive and no braces on a single statement, strict UTF-16, zeroing the blob copy, more tests, UserTests expectations) were fixed afterwards and reviewed in the pre-commit review (a new loop): approved, no 🔴/🟠; its three minors (UserTests wording, a redundant attribute) were fixed without a further review. The maintainer ran the tests on Windows: 412 passed, 0 failed, 9 skipped (8 `ScriptRunnerTests` that need a bash fake ssh, 1 off-Windows test), so the 4 Windows-only tests ran and passed.

- Task 5.1 (not committed): `ScriptRunner.RunAsync` gained an optional last parameter `ScriptMacroExpansion? expansion = null`: the expanded text is sent in place of the body (with `set -e` still prepended), every value to mask is replaced by `***` in each output line before the caller's callback, and a failed expansion throws before ssh starts; `BuildScriptText` gained the same optional parameter. The existing call in `ScriptsView` is unchanged. 5 new tests in `ScriptRunnerTests` (they use the bash fake ssh: they run on Linux only). Review: one iteration, no 🔴/🟠; the helper `Expanded` was moved with the other test helpers. `dotnet build MutagenMon.App -m:1` succeeds.
- Note carried to 5.3: expand exactly the `Body` of the `ScriptDefinition` passed to `RunAsync` (the edited draft), not the text of the file.

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| `dotnet test MutagenMon.Core.Tests -m:1` | all pass, no warning | 422 passed, 4 skipped (Windows-only), 0 failed, 0 warning (Linux) | pass |
| `dotnet build MutagenMon.App -m:1` | succeeds, no warning | succeeded, 0 warning | pass |
| Windows-only tests and the Windows code paths (registry, Credential Manager) | pass on Windows | not run (Linux sandbox) | not run |

### Errors
| Error | Resolution |
|-------|------------|
| Several edits reported as applied were not on disk (plan, spec, tests, reader), in Phases 2, 3 and 4 | Cause found in Phase 4: several `Edit` calls on the same file sent in one block run in parallel and overwrite each other. Detected each time by grep or a failing test, re-applied, verified. From now on one `Edit` per file per block. |
| The spec Implementation entry listing the Phase 3 classes, announced as written, was missing from the committed spec | Added in Phase 4 |
