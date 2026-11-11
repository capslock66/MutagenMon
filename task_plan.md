# Task Plan: Script macros (env, session, reg, cred, secret)

Source of truth: `plan-script-macros.md` (validated design, decisions 1-18). This file only sequences the work; do not duplicate the design here.

## Goal
Scripts of the Scripts tab can use `{{env:…}}`, `{{session:…}}`, `{{reg:…:…}}`, `{{cred:…}}` and `{{secret:…}}` macros (optional `|quote`), expanded locally just before the ssh run, with values masked in the output and logs, and no secret stored in the scripts file.

## Next Step
Phase 7 (delivery summary; ask before deleting `plan-script-macros.md` and the planning files), only when the user asks.

## Current Phase
Phase 6 complete; Phase 7 pending

## Working rules (from CLAUDE.md / memory)
- Work on `main`; commit only when asked, via `/eortc-git-push` (no body, no footer, no trailers).
- Code is ground truth; C# style: single-statement `if`/`for`/`foreach` without braces, body on its own line.
- Tests adapt to code: no single-implementation interfaces, use delegates/mocks/subclass seams.
- Build per project with `-m:1` (`dotnet test MutagenMon.Core.Tests/MutagenMon.Core.Tests.csproj -m:1`, `dotnet build MutagenMon.App/MutagenMon.App.csproj -m:1`); never `dotnet build MutagenMon.sln`.
- Each implemented FR also updates `requirements/UserTests.md`.
- After each batch of code changes: `eortc-dotnet-code-reviewer` loop (max 3 iterations), then summarise and stop. Never start the next phase without an explicit request.

## Phases

### Phase 1: Requirements & Discovery
- [x] 1.1 Understand user intent (macros without secrets in the scripts file)
- [x] 1.2 Identify constraints (BatchMode=yes, Core testable on Linux, FR-53.2 relaxed)
- [x] 1.3 Document in `plan-script-macros.md` (decisions 1-18)
- **Status:** complete

### Phase 2: Spec and user tests
- [x] 2.1 Reword FR-53.2/FR-53.3 in `requirements/11-scripts-tab-requirements.md` (all five macro types, file still holds no secret)
- [x] 2.2 Add FR-55 "Macros": syntax, env source and registry lookup, session property list, `reg`, `cred`, `secret` (one grid dialog), raw insertion and `|quote`, refused values, failure before run in the status, masking (FR-55.1 to FR-55.11; the session property list stays for 4.3)
- [x] 2.3 Fix FR-47.3 (script text is editable, not read-only) — already correct in the working tree, nothing to change
- [x] 2.4 Update `requirements/UserTests.md` with the scenarios listed in plan section 6 (UT-55.11 to UT-55.14, marked ⏳)
- [x] 2.5 Fix the inconsistencies left in `plan-script-macros.md` (decisions 1 and 7, section 6, 4.3/4.4 lists)
- **Status:** complete

### Phase 3: Core parser, expander, masker
- [x] 3.1 `MutagenMon.Core/Scripts/ScriptMacro.cs`: record and commented regex parser `Find(text)`, distinct `secret` labels
- [x] 3.2 Shared shell-quote helper (extracted from `ConflictFileClient.ShellQuote` into `MutagenMon.Core/Ssh/ShellQuoting.cs`)
- [x] 3.3 `ScriptMacroExpander.Expand(...)`: delegates for env, reg, cred, secret; selected session record; errors; values to mask; line break/NUL refused
- [x] 3.4 Output masker (`***`, exact match) in `ScriptOutputMasker.cs`
- [x] 3.5 Unit tests in `MutagenMon.Core.Tests` (386 tests pass)
- **Status:** complete (review loop: 3 iterations, then a pre-commit review that approved the last fix; see progress.md)

### Phase 4: Windows readers
- [x] 4.1 Registry reader: `env` (HKCU then HKLM, `REG_EXPAND_SZ`, process-environment fallback) and `reg` (hives, value types), `null` off Windows
- [x] 4.2 Credential Manager reader (`CredReadW` P/Invoke, generic credential, UTF-16 blob), `null` off Windows
- [x] 4.3 Enumerate the session properties from the Sync grid model and write the list into FR-55 (table in FR-55.3)
- **Status:** complete (review loop: 3 iterations plus a pre-commit review, no 🔴/🟠; tests run on Windows by the maintainer: 0 failed; see progress.md)

### Phase 5: Runner and App integration
- [x] 5.1 `ScriptRunner.RunAsync`: receive expanded text and values to mask; mask output before `onOutput` and logs; logs keep the macro form (optional last parameter `ScriptMacroExpansion? expansion`; a failed expansion is refused)
- [x] 5.2 `secret` dialog: one WPF dialog with a grid (one row per distinct label, `PasswordBox`, OK/Cancel)
- [x] 5.3 `ScriptsView`: selected session, expander call on Run, errors in `StatusText`, no run on error or cancel; `@confirm` keeps its usual text (never the expanded script) plus the resolved session name. Expand exactly the `Body` of the `ScriptDefinition` passed to `RunAsync` (the edited draft, FR-51.1), not the text of the file
- **Status:** complete (5.1 committed `9332eb1`; 5.2 and 5.3 reviewed in 3 iterations, no 🔴/🟠 left; not committed; see progress.md)

### Phase 6: Testing & Verification
- [x] 6.1 `dotnet test` on `MutagenMon.Core.Tests` and `dotnet build` on `MutagenMon.App` (each with `-m:1`) — Linux: 422 passed, 4 skipped, 0 failed, App build 0 warning; Windows (maintainer): 426 total, 414 passed, 0 failed, 12 skipped, App Release build succeeded
- [x] 6.2 `eortc-dotnet-code-reviewer` loop on the modified files (spec `requirements/11-scripts-tab-requirements.md`, max 3 iterations) — 2 iterations, no 🔴/🟠; minors fixed or listed in progress.md
- [x] 6.3 Manual scenarios from `UserTests.md` for the Windows-only parts (registry, Credential Manager, dialog)
- **Status:** complete (6.1, 6.2 and 6.3 done)

### Phase 7: Delivery
- [ ] 7.1 Summarise what was done, iterations of the review loop, remaining minor findings
- [ ] 7.2 Ask before deleting `plan-script-macros.md` and the planning files
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| Spec before code | FR-53.2 is deliberately relaxed; the spec must change first |
| Core takes delegates, App provides Windows readers and the dialog | Core stays testable on Linux (NFR-11), no single-implementation interfaces |
| Phases 3 to 5 are separate batches | Each batch ends with the review loop and a stop |

## Errors Encountered
| Error | Resolution |
|-------|------------|
