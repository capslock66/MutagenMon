# Task Plan: Update detection (FR-66)

## Goal
MutagenMon checks every 2 hours whether `MutagenMon.exe` in a configured deploy folder is newer than the running one, shows an "Update" button in the status window toolbar (right of "Exit mutagen monitor"), and on confirmation replaces itself while running and restarts.

## Next Step
Phase 5: manual tests UT-66.x on Windows, then commit on request.

## Current Phase
Phase 5

## Phases

### Phase 1: Requirements & Discovery
- [x] Read toolbar XAML (`StatusWindow.xaml` Grid.Row=1), `ConfigLoader.EnsureHaltedKeys`, `SelfRestart`, deploy .bat scripts
- [x] Document constraints and design options in findings.md
- [x] User answers open questions (see findings.md §Decisions)
- **Status:** in_progress

### Phase 2: Requirements documents
- [x] New `requirements/13-update-detection-requirements.md` (FR-66.x), indexed in `requirements/README.md`
- [x] `requirements/06-configuration-reference.md`: add `DeployPath` row
- [x] `requirements/09-mutagen-monitor-config-editor-requirements.md`: add field to editor
- [x] `requirements/UserTests.md`: UT-66.x (per project rule: every implementation updates UserTests)
- **Status:** complete

### Phase 3: Core (MutagenMon.Core)
- [x] `MutagenMonOptions.DeployPath` (default `""`) and `NotifyUpdateAvailable` (default true); both added at startup when absent
- [x] `ConfigLoader`: add `DeployPath` at startup when absent (extend the `EnsureHaltedKeys` mechanism or a generic `EnsureMissingKeys`)
- [x] `UpdateChecker`: compare deployed exe vs running exe (last-write time, fallback file version); unit tests
- **Status:** complete

### Phase 4: App (MutagenMon.App)
- [x] 2 h `DispatcherTimer` (+ one check at startup) in `App.xaml.cs`; skip when `DeployPath` empty or folder unreachable (log only, no popup)
- [x] `UpdateButton` in `StatusWindow.xaml` right of `ExitButton`, hidden unless an update is available
- [x] Confirmation dialog (existing `GenericMessageDialog`)
- [x] Config editor field "Deploy path" in `MutagenMonitorConfigEditorView`
- [x] Update mechanism (see findings.md): helper that waits for exit, copies, restarts
- **Status:** complete

### Phase 5: Verification
- [x] Build per project (`dotnet build -m:1` on each .csproj) and run Core tests
- [x] Review loop with `eortc-dotnet-code-reviewer`
- [ ] Manual tests UT-66.x on Windows
- **Status:** in_progress

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| New requirement number FR-66, new doc 13 | Last used is FR-65 (doc 12) |
| Config key `DeployPath`, PascalCase | Matches `MutagenMonOptions` convention (doc 06) |
| Default `""` = feature disabled | Per request |
| Missing key is added at startup by text edit | Same mechanism as FR-63.1 (keeps `#` comments) |

| Toolbar button + notification (first detection only) | User choice; new key `NotifyUpdateAvailable` |
| Compare by LastWriteTimeUtc; copy as the .bat does | User choice |

## Errors Encountered
| Error | Resolution |
|-------|------------|
