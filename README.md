# MutagenMon

MutagenMon is a Windows desktop utility that supervises your
[mutagen.io](https://github.com/mutagen-io/mutagen) file-synchronization
sessions and tells you, at a glance, whether everything is in sync.

It is a .NET WPF rewrite of [rualark/MutagenMon](https://github.com/rualark/MutagenMon).

## Main features

### Real-time status in the system tray

- A tray icon shows the overall health of all your sync sessions — it always
  reflects the *worst* session — and its tooltip gives the details.
- The icon distinguishes the main states: waiting for status, scanning,
  syncing, ready (all in sync), conflicts, and problems.
- If MutagenMon stops getting fresh data from mutagen, the icon degrades
  progressively (info → warning → error), and the application restarts
  itself when the data stays stale for too long.
- Left-click the icon, or right-click → **Show status**, to open the status
  window.

### Status window

- One block per session with its name, status, and the Alpha / Beta endpoints.
- A **Conflicts** section listing every unresolved conflict, with a
  **Resolve conflicts** button.
- A toolbar and per-session actions to add, edit, delete, or view the sync
  status of a session.
- A **Logs** tab showing application events in real time, with colored
  warnings/errors, a detail pane, and buttons to clear or open the log file.

### Conflict resolution

- **Manual:** conflicts are presented one at a time ("N of total") with an
  A/B comparison (location, size, last-modified date). Choose *Visual merge*
  (opens your diff/merge tool), *A wins*, or *B wins*; the side modified most
  recently is pre-selected. Every resolution is recorded in `log/resolve.log`.
- **Automatic:** define ordered regular-expression rules (`AutoResolve`) on
  file names; the first matching rule resolves the conflict immediately.

### Notifications

- A desktop toast announces new conflicts.
- A toast announces each automatically resolved conflict, naming the rule and
  the file.
- Both can be turned off in the configuration (`NotifyConflicts`,
  `NotifyAutoresolve`).

### Session management and configuration editors

- Create and edit mutagen sessions from a form: Alpha / Beta paths, sync
  mode, ignore patterns, permissions, symbolic links, watching, probing and
  scanning, staging, and limits.
- Edit the mutagen configuration (YAML) and the MutagenMon configuration
  (including auto-resolve rules and SSH servers) directly in the application,
  with a **Check** button to validate before saving.
- **Reload config & restart mutagen** applies your changes to the running
  sessions.

### Tray menu

- **Show status** — open the status window.
- **Move to main screen** — bring the window back if it ended up off-screen.
- **Stop / Start Mutagen sessions** — pause or resume monitoring; the tray
  icon shows a "disabled" variant while paused.
- **Reload config & restart mutagen** — restart every session with the
  current configuration.
- **Exit MutagenMon** — close the application and remove the tray icon.

## Getting started

1. Install a [mutagen.io](https://github.com/mutagen-io/mutagen) release and
   place `mutagen.exe` in the `mutagen` folder next to `MutagenMon.exe`, or
   point `MutagenPath` to it in `config/config_mutagenmon.json`.
2. Define your sessions in `mutagen/mutagen-create.bat` or through the
   session editor in the status window.
3. Start `MutagenMon.exe`. The tray icon appears right away and updates as
   soon as mutagen reports its status.

## Configuration

Settings live in `config/config_mutagenmon.json` (or in the in-app
configuration editor): the path to mutagen, polling and staleness thresholds
(`StatusMaxLag`), notifications, auto-resolve rules, and the log folder
(`LogPath`).

## Troubleshooting

- Logs are written to `log/mutagenMon.log` (folder configurable with
  `LogPath`; verbosity with `MinLogLevel`).
- If the tray icon never appears, MutagenMon shows an error message at
  startup — typically a missing or invalid `config/config_mutagenmon.json`,
  `mutagen/mutagen-create.bat`, or `MutagenPath`.
- If no log file exists either, open Windows Event Viewer → Windows Logs →
  Application and look for the source **MutagenMon**.
