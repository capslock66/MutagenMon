# Task Plan: `{{prompt:<Label>}}` macro (typed value, not masked)

## Goal
Add a macro type `prompt` to the Scripts tab: like `{{secret:<Label>}}` it asks the user for a value in the single pre-run dialog, but the input is a visible text box and the value is NOT masked in the output.

## Next Step
Task 5.1: port the ScriptMacroTests of `SecretLabels` to `ValueRequests` and add the prompt/default parsing tests (Phase 5).

## Current Phase
Phase 5

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
- [x] 2.5 Default value (added after phase 2): FR-55.1 (syntax of the modifiers) and FR-55.12: `{{prompt:<Label>|default=<value>}}`, combinable with `|quote`; dialog pre-filled and selected; same label with different defaults is a macro error; `secret` refuses `default`; empty `|default=` = no default; the default cannot contain `|` or `}}`; empty value after clearing the field is still refused
- **Status:** complete

### Phase 3: Core implementation (MutagenMon.Core)
- [x] 3.1 `ScriptMacro.cs`: add `ScriptMacroType.Prompt`, map `"prompt"` in `Type`
- [x] 3.2 `ScriptMacro.cs`: replace `SecretLabels` by a method returning the distinct requested inputs (kind + label, first-appearance order, one row per label): added as `ValueRequests`; `SecretLabels` kept for now (existing tests), remove in 5.1 with the port of its tests (the App no longer uses it)
- [x] 3.3 `ScriptMacroSources`: add the `Prompt` lookup delegate
- [x] 3.4 `ScriptMacroExpander.Resolve`: handle `Prompt` (empty name error, empty value error, `isMasked: false`); update "known types" error text; keep `|quote` and the line-break/NUL refusal
- [x] 3.5 `ScriptMacroExpander`: detect a label used by both `secret` and `prompt` and report one macro error naming the label and both types (reported by the pre-check, so no dialog opens)
- [x] 3.6 Default value: `ScriptMacro` regex and record: parse the modifiers `|default=<value>` and `|quote` (any order, each at most once), new `Default` property (null when absent or empty); update the regex comment; `Display` rebuilds the source form
- [x] 3.7 Default value: `ScriptValueRequest` gets a `Default` (the common default of the macros of the label); `ValueRequests` fills it
- [x] 3.8 Default value: `ScriptMacroExpander` errors: `default` on a `secret` macro, and several different defaults for one `prompt` label (reported by the pre-check, names the label never a value other than the default text)
- **Status:** complete

### Phase 4: App implementation (MutagenMon.App)
- [x] 4.1 `SecretValuesDialog`: accept inputs of two kinds; `PasswordBox` for secret, visible `TextBox` for prompt; adapt Focus/OK/clear logic; update title and intro text
- [x] 4.2 `ScriptRunCoordinator.ExpandMacros`: ask for both kinds in one dialog, pre-check with placeholders for both, build the two lookups, update the cancel message
- [x] 4.3 Logging: keep "never log values nor labels" (dialog logs counts only; the pre-filled default is not logged)
- [x] 4.4 Default value: `SecretValuesDialog` pre-fills the `TextBox` of a `prompt` row with its default and selects the text on focus; the returned value is the field content
- **Status:** complete

### Phase 5: Tests
- [ ] 5.1 `ScriptMacroTests`: `prompt` type parsing (plain and `|quote`), comment handling, labels list with mixed kinds
- [ ] 5.2 `ScriptMacroExpanderTests`: prompt resolved, NOT in `SecretsToMask`; empty/missing error; same label in `secret` and `prompt` gives a macro error naming the label (no value in the message); `|quote`; line break refused
- [ ] 5.5 Default value, `ScriptMacroTests`: parsing of `|default=`, with and without `|quote`, either order, empty default, default containing `:` or `=`, `Default` in `ValueRequests`
- [ ] 5.6 Default value, `ScriptMacroExpanderTests`: `default` on `secret` is an error; two different defaults for one label is an error; identical defaults are accepted; the default is never used as the resolved value (the lookup decides)
- [ ] 5.3 `requirements/UserTests.md`: add UT-55.15 (`{{prompt:…}}`, mixed dialog, visible input, unmasked output, cancel, empty, pre-filled default, conflicting defaults, default on a `secret`)
- [ ] 5.7 Doc/spec leftovers from the phase 4 review: `UserTests.md` wording "secret dialog" -> "values dialog" and the new cancel message (UT-55.14 and the line ~2005), `ScriptMacroSources`/`ScriptMacroExpander` XML doc mentions the `prompt` values, remove `ScriptMacro.SecretLabels`, FR-55.1: state that a repeated `|quote` on a non-prompt macro stays part of its name (or validate otherwise)
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
| Default value syntax `{{prompt:<Label>\|default=<value>}}` | `\|` already introduces modifiers (`\|quote`); the label stays intact (it may contain `=` or `:`). Validated by the user |
| `secret` refuses `default` | A default would be stored in clear text in the scripts file (FR-53.3) |
| Same label with different defaults is an error | Same logic as the secret/prompt label conflict: one label, one value |
| `ScriptMacro.IsRejected`/`SafeName`: a malformed token or a default on a non-prompt macro is reported and never resolved; messages never carry the default text of a non-prompt macro | FR-53.3: a secret typed by mistake in a default must not reach the log (review finding) |
| `|quote` repeated on a non-prompt macro stays part of its name | Backward compatibility with existing macros; to mention in FR-55.1 (open point) |
| Default cannot contain `\|` or `}}`; empty `\|default=` = no default | Parsing limits of the token (FR-55.1) |

## Errors Encountered
| Error | Resolution |
|-------|------------|
