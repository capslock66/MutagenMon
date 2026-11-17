# Task Plan: `{{prompt:<Label>}}` macro (typed value, not masked)

## Goal
Add a macro type `prompt` to the Scripts tab: like `{{secret:<Label>}}` it asks the user for a value in the single pre-run dialog, but the input is a visible text box and the value is NOT masked in the output.

## Next Step
Task 4.1 (reviewer: trigger pre-check and dialog on ValueRequests, placeholder for both Secret and Prompt in 4.2): SecretValuesDialog with PasswordBox/TextBox rows (Phase 4). Note: MutagenMon.App does not compile until 4.2 (ScriptMacroSources has a new Prompt delegate).

## Current Phase
Phase 4

## Phases

### Phase 1: Requirements & Discovery
- [x] 1.1 Locate the code: `ScriptMacro.cs`, `ScriptMacroExpander.cs`, `ScriptRunCoordinator.cs` (`ExpandMacros`), `SecretValuesDialog`
- [x] 1.2 Locate the spec (FR-55.1, FR-55.8, FR-55.11 in `requirements/11-scripts-tab-requirements.md`) and UT-55.14
- [x] 1.3a Label rule validated: a label cannot be used by both `secret` and `prompt`
- [x] 1.3b Validated: an empty `prompt` value is refused
- **Status:** complete

### Phase 2: Spec first (requirements)
- [x] 2.1 `11-scripts-tab-requirements.md` FR-55.1: add `prompt` to the list of types
- [x] 2.2 Add FR-55.12 `{{prompt:<Label>}}` (typed at run time, visible input, never stored, not masked, same dialog as FR-55.11, empty value unresolved, Cancel cancels the run); state in FR-55.11/55.12 that a label cannot be shared between a `secret` and a `prompt` macro (macro error, FR-55.6)
- [x] 2.3 FR-55.8 (masking): state that `prompt` values are not masked; FR-55.11: dialog now serves `secret` and `prompt` rows
- [x] 2.4 Update the code-map section (SecretValuesDialog description, message "Run cancelled")
- **Status:** complete

### Phase 3: Core implementation (MutagenMon.Core)
- [x] 3.1 `ScriptMacro.cs`: add `ScriptMacroType.Prompt`, map `"prompt"` in `Type`
- [x] 3.2 `ScriptMacro.cs`: replace `SecretLabels` by a method returning the distinct requested inputs (kind + label, first-appearance order, one row per label): added as `ValueRequests`; `SecretLabels` kept for now (existing tests), remove in 4.2 if unused
- [x] 3.3 `ScriptMacroSources`: add the `Prompt` lookup delegate
- [x] 3.4 `ScriptMacroExpander.Resolve`: handle `Prompt` (empty name error, empty value error, `isMasked: false`); update "known types" error text; keep `|quote` and the line-break/NUL refusal
- [x] 3.5 `ScriptMacroExpander`: detect a label used by both `secret` and `prompt` and report one macro error naming the label and both types (reported by the pre-check, so no dialog opens)
- **Status:** complete

### Phase 4: App implementation (MutagenMon.App)
- [ ] 4.1 `SecretValuesDialog`: accept inputs of two kinds; `PasswordBox` for secret, visible `TextBox` for prompt; adapt Focus/OK/clear logic; update title and intro text
- [ ] 4.2 `ScriptRunCoordinator.ExpandMacros`: ask for both kinds in one dialog, pre-check with placeholders for both, build the two lookups, update the cancel message
- [ ] 4.3 Logging: keep "never log values nor labels" (dialog logs counts only)
- **Status:** pending

### Phase 5: Tests
- [ ] 5.1 `ScriptMacroTests`: `prompt` type parsing (plain and `|quote`), comment handling, labels list with mixed kinds
- [ ] 5.2 `ScriptMacroExpanderTests`: prompt resolved, NOT in `SecretsToMask`; empty/missing error; same label in `secret` and `prompt` gives a macro error naming the label (no value in the message); `|quote`; line break refused
- [ ] 5.3 `requirements/UserTests.md`: add UT-55.15 (`{{prompt:…}}`, mixed dialog, visible input, unmasked output, cancel, empty)
- [ ] 5.4 Build per project (`-m:1`) and run `MutagenMon.Core.Tests`
- **Status:** pending

### Phase 6: Review & Delivery
- [ ] 6.1 Run `eortc-dotnet-code-reviewer` loop on the changes (spec: FR-55)
- [ ] 6.2 Fix findings, summarise, stop (no commit unless asked)
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| One single dialog for `secret` and `prompt`, rows in order of first appearance | Consistent with FR-55.11 (one dialog per run); user did not ask for a second one |
| `prompt` input is a visible `TextBox`; values not masked in output (`isMasked: false`, not in `SecretsToMask`) | Requested behaviour |
| A label cannot be used by both `secret` and `prompt`: macro error before the run, no dialog | Validated by the user; avoids showing a secret in a visible box |
| Empty `prompt` value is unresolved (same as `secret`) | Consistency with FR-55.6/55.11. Validated by the user |
| `|quote`, line-break/NUL refusal, comment handling apply unchanged | Generic expander behaviour (FR-55.4/55.5/55.1) |
| Not stored, not logged, not reused by later runs | Same as FR-55.11 |

## Errors Encountered
| Error | Resolution |
|-------|------------|
