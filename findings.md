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
