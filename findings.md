# Findings & Decisions

## Requirements
- Use a secret or context value in a script without storing it in the scripts file (`plan-script-macros.md`, section 1).
- Five macro sources: `env`, `session`, `reg`, `cred`, `secret`; optional `|quote`.
- Run does not start on any unresolved or refused macro; values from `env`/`reg`/`cred`/`secret` masked as `***` in the output and its logs.

## Research Findings
- Source plan: `plan-script-macros.md` (validated, nothing implemented; decisions 1-18).
- Spec to change: `requirements/11-scripts-tab-requirements.md` (FR-47.3, FR-53.2, FR-53.3, new FR-55) and `requirements/UserTests.md`.
- Existing code to touch: `MutagenMon.Core/Scripts/ScriptRunner.cs` (stdin write, `RedirectStandardInput`), `MutagenMon.App` `ScriptsView.xaml`/`.xaml.cs`, `ConflictFileClient.ShellQuote` (to share in Core).
- `HELPER_MERGE_PLAN.md` no longer exists in the repo.

## Technical Decisions
| Decision | Rationale |
|----------|-----------|
| `{{type:name}}` syntax, last `:` splits `reg` key and value name | No collision with bash or `[section]` headers |
| Raw insertion by default, `|quote` opt-in | Some bash commands break when quotes are added |
| One grid dialog for all `secret` labels | One interaction, one cancel, no half-asked state |
| Windows readers return `null` off Windows | Core tests run on Linux |

## Open points carried from the Phase 3 reviews
| Point | For |
|-------|-----|
| The delegates of `ScriptMacroSources` must return `null` instead of throwing (`SecurityException`, `UnauthorizedAccessException`...): readers catch their own errors | Phase 4 (4.1, 4.2) |
| `{{session:LastChangedDisplay}}` uses the local time zone and culture; document in FR-55.3 that `LastChangedUtc` (ISO 8601) is preferable | Task 4.3 |
| The secret dialog is shown before `Expand`: a missing env variable or no selected session makes the user type secrets for nothing; consider a pre-validation without the `Secret` delegate | Phase 5 |
| The masker works line by line: `ScriptRunner` must pass whole lines, not stream chunks | Task 5.1 |
| `GetProperty(..., IgnoreCase)` would throw if `SessionSummaryRow` ever gets two properties differing only by case | If the record changes |

## Open questions for the maintainer (from the Phase 4 reviews)
| Question | Current behaviour |
|----------|-------------------|
| `{{env:Path}}`: Windows builds a new process `Path` as machine value then user value, concatenated | The user value alone when it exists (FR-55.2 "user overrides machine") |
| A `%X%` reference inside a machine value is resolved user-first | Same lookup for every reference (FR-55.2 "read the same way") |
| Two references stuck together after an unknown one (`%NOPE%HOME%`): the closing `%` is reused as the next opening one | The rule of the managed `Environment.ExpandEnvironmentVariables`, not compared with the Windows `ExpandEnvironmentStrings` |
| An empty user `REG_SZ` variable | Returned as an empty string (does not fall back to the machine value); the expander then reports it as unresolved |

## Issues Encountered
| Issue | Resolution |
|-------|------------|
| `plan-script-macros.md`: decision 1 lists only `env`/`session` | Fix in Phase 2 |
| Decision 7 says only env values are masked (reg, cred, secret also are) | Fix in Phase 2 |
| Section 6 and 4.3/4.4 mention only `env`/`session` | Fix in Phase 2 |
| Open: exact list of session properties | Enumerate from the Sync grid model in Phase 4 |
| Open: also write the status error to the output pane? (default: status line only) | Keep the default unless the user asks |

## Resources
- `plan-script-macros.md`
- `requirements/README.md`, `requirements/11-scripts-tab-requirements.md`, `requirements/UserTests.md`
- `CLAUDE.md` (C# style, commits)
