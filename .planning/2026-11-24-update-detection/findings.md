# Findings

## Existing code relevant to the change
- Toolbar: `MutagenMon.App/StatusWindow.xaml` ~line 208, `Grid Row=1`, a left-aligned `StackPanel` with `ReloadConfigButton` then `ExitButton` ("Exit mutagen monitor", `OnExitClick`). The new button goes after `ExitButton` in that panel ("tout à droite"). The tab-level "Close" button shown in older docs is not in this XAML.
- Config: `MutagenMon.Core/Configuration/MutagenMonOptions.cs` + `ConfigLoader.cs`. `EnsureHaltedKeys`/`ApplyMissingHaltedKeys` already add absent keys at startup by text edit (comments preserved). Reuse for `DeployPath`.
- Config editor: `MutagenMonitorConfigEditorView.xaml(.cs)` (FR-34..38, doc 09); doc 06 is the key reference.
- `SelfRestart.RestartAndExit()` spawns `Environment.ProcessPath` then `Application.Shutdown()`.
- Deploy flow today: `build-release-deploy.bat` builds Release, copies `bin\Release\net10.0-windows\*.*` + `Assets\` to `\\prod-build\DATA\install\Mutagen-mon` and to `c:\tools\Mutagen-Mon`, deliberately NOT `config\` nor `mutagen\` (per-developer settings). The updater must copy the same set: top-level files + `Assets\`.
- No existing periodic 2 h timer; `DispatcherTimer` is the pattern used (TrayIconController).

## Design: updating while the program runs
Windows locks a running `.exe`/`.dll` against overwrite and delete, but allows renaming it.
- **Option A (recommended): helper script.** On confirm, write a small `.cmd`/PowerShell script to `%TEMP%`, start it hidden, and shut MutagenMon down. The script waits for the process to exit (poll `tasklist`/wait on PID), copies top-level files + `Assets\` from `DeployPath` to the install folder (same exclusions as the .bat), restarts `MutagenMon.exe`. Simple, no leftover files; app is down for a few seconds. Mutagen sessions keep running (the "Exit" dialog already states this).
- **Option B: rename trick.** Rename running `MutagenMon.exe` (and changed DLLs) to `*.old`, copy the new files, restart, delete `*.old` at next startup. No helper script, but leaves `.old` files and needs cleanup logic; partial-failure states are harder.
- **Option C: separate updater exe** shipped with the app. Most robust, but adds a project and a second binary to deploy; overkill here.

## Behavior decisions to specify (FR-66.x)
- Comparison: `LastWriteTimeUtc` of `<DeployPath>\MutagenMon.exe` > that of the running exe (`Environment.ProcessPath`). Copy preserves mtime, and the deploy script copies with `copy /Y`, so mtime is comparable; file version would be more reliable only if the assembly version is bumped per build (it is not known to be).
- Check at startup and every 2 h; unreachable share/empty path = silent skip (log at Debug/Warning once, no popup). The button disappears if a later check finds no update.
- Config reload (`Reload config & restart`) re-reads `DeployPath`.
- Config editor: one text field "Deploy path" (+ Browse folder) in the monitor config editor; save validation: empty OR existing folder (warn only, since a share may be temporarily offline).
- Button label e.g. "Update available…"; confirm dialog states deployed vs current timestamp, that MutagenMon will restart, and that sessions keep syncing.

## Decisions (all open questions closed)
1. Mechanism: Option A (helper script) - confirmed.
2. Comparison: file modification time (LastWriteTimeUtc) of MutagenMon.exe.
3. Signal: toolbar button AND a Windows notification the first time an update is detected (not repeated at each 2 h check for the same deployed version; gated like other notifications - add a `NotifyUpdateAvailable` key, default true, to doc 06 / config editor).
4. Files copied: same as build-release-deploy.bat (top-level files + Assets\; never config\ or mutagen\).
