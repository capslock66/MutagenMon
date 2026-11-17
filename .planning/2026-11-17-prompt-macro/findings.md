# Findings & Decisions

## Requirements
-

## Research Findings
-

## Technical Decisions
| Decision | Rationale |
|----------|-----------|

## Issues Encountered
| Issue | Resolution |
|-------|------------|

## Resources
-

## Findings (2026-11-17)
- `ScriptMacroType` enum + `Type` switch: `MutagenMon.Core/Scripts/ScriptMacro.cs`; `SecretLabels()` feeds the dialog.
- `ScriptMacroExpander.Resolve`: `FromLookup` always masks (`isMasked: true`); `Session` is the unmasked precedent (`Resolved(..., isMasked: false)`).
- `ScriptMacroSources` is a record with delegates (Environment, Registry, Credential, Secret, SelectedSession): add `Prompt`; the test helper `Sources(...)` in `ScriptMacroExpanderTests` constructs it positionally and must be updated.
- `ScriptRunCoordinator.ExpandMacros` (~line 285): pre-check with placeholder `"x"`, then `SecretValuesDialog.Ask(owner, logger, labels)`; cancel message "Run cancelled: the secret values were not entered."
- `SecretValuesDialog`: rows built in code (PasswordBox rows); title "MutagenMon: Script secrets".
- Spec: FR-55.1 (types list), FR-55.8 (masking), FR-55.11 (secret + dialog); UT-55.14 is the secret test.
- Build per project on Linux; always `-m:1` (user rules).
