# Update Detection — Requirements & Design

This document specifies how MutagenMon detects that a newer build has been
deployed and lets the user update the running installation in place.

**Status: implemented and tested** (manual tests UT-66.x in
[UserTests.md](UserTests.md)). Continues the
FR numbering from [12-halted-sessions-requirements.md](12-halted-sessions-requirements.md)'s
FR-65, as FR-66.

## Background

`build-release-deploy.bat` builds MutagenMon in Release mode and copies the
output (top-level files and `Assets\`) to a shared deploy folder
(`\\prod-build\DATA\install\Mutagen-mon`) and to the developer's own
installation. Every other installation had to be updated by hand. This
feature lets an installation notice the new build and update itself.

## Requirements

- FR-66.1: The configuration has two keys, documented in
  [06-configuration-reference.md](06-configuration-reference.md):
  `DeployPath` (string, default empty) and `NotifyUpdateAvailable`
  (boolean, default `true`). When either is absent from the config file at
  startup, it is added with its default value (same text-editing approach
  as FR-63.1: `#` comments and every other key are kept, an existing value
  is never modified, a file that cannot be written only costs a warning).
  Both are editable in the monitor configuration editor
  ([09-mutagen-monitor-config-editor-requirements.md](09-mutagen-monitor-config-editor-requirements.md)):
  "Deploy path" (with a folder browser) in the Paths tab and "Notify on update
  available" in the Logging & notifications tab.
- FR-66.2: An empty `DeployPath` disables the feature. Otherwise MutagenMon
  checks once at startup, once after each "Reload config & restart", and
  every 2 hours. An update is available when
  `<DeployPath>\MutagenMon.exe` has a later last-write time (UTC) than the
  running executable. Environment variables in `DeployPath` are expanded.
- FR-66.2a: After each check, when `DeployPath` is not empty, an information
  line in the log gives the local time of the next scheduled check ("Next
  scheduled update check at yyyy-MM-dd HH:mm:ss"). A check started by a
  reload or by the user does not move that time.
- FR-66.3: The check never blocks the UI (the folder may be a network
  share) and never raises: an empty, missing or unreachable folder, or a
  missing `MutagenMon.exe`, means "no update known" with no dialog. The
  reason is logged as a warning (once per distinct reason, so the 2-hour
  checks do not repeat it), and the next scheduled check tries again. A
  relative `DeployPath` is resolved against the application folder. A check
  requested while another one is still running (e.g. after a reload that
  changed `DeployPath`) runs as soon as the first one ends.
- FR-66.4: A check that finds no update (for example after the deployed file
  was replaced by an older one) turns the button of FR-66.6 back into
  "Check for new version".
- FR-66.5: The first time a given deployed build is detected, and when
  `NotifyUpdateAvailable` is `true`, a desktop notification "Update
  available" is shown. The same deployed build is not notified again by the
  following checks. A build detected while `NotifyUpdateAvailable` is `false`
  is still notified once if the option is later switched on.
- FR-66.6: The shared toolbar at the bottom of the status window has
  "Reload config & restart" and the update button on the left, and "Exit
  mutagen monitor" at the far right. The update button is visible
  whenever `DeployPath` is not empty and hidden otherwise. It reads "Check
  for new version" until an update is available, then "Update available",
  displayed on a light blue background with bold text so that it stands out
  from the other toolbar buttons (default look again when no update is
  available). Like the other toolbar buttons it is disabled during a config reload, and
  it is also disabled while its own check runs. An update requested while a
  reload or an update check is running (for instance by a check that ends
  during a reload) is not started: it is logged and the button keeps its
  label.
  Clicking "Check for new version" runs the check of FR-66.2 now: when a
  newer build is found, the confirmation dialog of FR-66.7 opens directly
  (the notification of FR-66.5 also applies the first time that build is
  seen, and if the user cancels, the button now reads "Update available");
  otherwise a message says that MutagenMon is up to date, or why no comparison
  was possible (FR-66.3).
- FR-66.7: The confirmation dialog (opened by "Update available", or directly
  by "Check for new version" when it finds a build) states the deployed and
  running timestamps, that MutagenMon will close and restart, and that
  background synchronization continues. On "Cancel" nothing changes. On
  "Update":
  1. the scripts with unsaved changes are saved first (FR-47.5); if that
     fails, the update is cancelled;
  2. a helper batch script is written to the temp folder and started hidden.
     It waits (up to 60 s) for the MutagenMon process to exit, copies the
     top-level files of the deploy folder, then its `Assets` folder, over the
     installation folder, and starts `MutagenMon.exe` again. Nothing is
     copied when the deploy folder has no `MutagenMon.exe` or no `Assets`
     folder; if a copy fails midway, the files already copied stay, so the
     installation may be partly updated (the log says so) and MutagenMon is
     started again regardless. If the process has not exited after 60 s,
     nothing is copied and MutagenMon is not started again (the running
     instance would still hold the single-instance mutex). The `config`
     and `mutagen` folders are never touched. The outcome is written to
     `MutagenMon-update.log` in the temp folder, and the script deletes
     itself;
  3. MutagenMon then shuts down as it does for "Exit mutagen monitor", without
     asking for a second confirmation. If the script cannot be started, a
     message says so and MutagenMon keeps running.

## Design notes

- Windows refuses to overwrite an executable that is running, so the files
  cannot be copied by the process itself. A helper script that outlives the
  process was preferred over renaming the running files (which leaves `.old`
  files to clean up) and over a separate updater executable (a second binary
  to deploy).
- The last-write time is compared rather than the assembly version: the
  version (see `Directory.Build.props`) changes with every build too, but the
  modification time is what `copy /Y` and `robocopy` preserve and needs no
  parsing of the file.
- The script file is UTF-8, so it starts with `chcp 65001` to keep accented
  paths (e.g. a Windows profile name in the temp folder) intact.
- Code: `MutagenMon.Core/Updates/UpdateChecker.cs`,
  `UpdateScriptBuilder.cs`; `App.xaml.cs` (timer, notification, flow);
  `StatusWindow.xaml` (button).
