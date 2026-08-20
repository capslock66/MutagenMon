# MutagenMon — Application Overview & Codebase Analysis

## 1. Purpose

MutagenMon is a cross-platform desktop utility that supervises one or more
[mutagen.io](https://github.com/mutagen-io/mutagen) file-synchronization
sessions. It runs as a background process with a **system tray icon** as its
primary (and, most of the time, only) user interface. Its purpose is to:

- start/monitor mutagen sync sessions defined by the user,
- surface the aggregated real-time health of all sessions through the tray
  icon and its tooltip,
- automatically restart sessions that hang, error out, or disappear,
- detect synchronization conflicts and help the user resolve them (manually
  via a visual diff/merge tool, or automatically via configurable path
  rules),
- notify the user (OS toast/balloon) about important events,
- self-heal (restart the whole application) if its own status monitoring
  goes stale or the tray icon fails.

## 2. Current technology stack

| Concern                  | Technology                                                              |
|---------------------------|--------------------------------------------------------------------------|
| Language                  | Python 3                                                                 |
| UI toolkit                | wxPython (`wx`, `wx.adv.TaskBarIcon`)                                    |
| Sync engine                | External `mutagen` CLI binary, invoked as a subprocess                  |
| Remote transport          | SSH / SCP external binaries (Git-for-Windows `ssh.exe` / `scp.exe`)      |
| Visual diff/merge          | External tool (WinMerge by default, configurable path)                 |
| Configuration              | Hand-edited JSON file with `#` comment lines stripped before parsing    |
| Session definitions         | Parsed out of a Windows `.bat` file containing `mutagen sync create` commands |
| Persistence / state         | In-memory only (per-process); flat log files on disk                   |
| Concurrency                 | One background `threading.Thread` (`Monitor`) polling the mutagen CLI, one wx UI/timer thread |
| Packaging                  | Single-instance Windows executable (`mutagenmon.pyw` / compiled `mutagenmon.exe`) |

The application has **no window** in the traditional sense — `wx.Frame(None)`
is created only to host the `TaskBarIcon` and is never shown. All
interaction happens through the tray icon, its context menu, and modal
dialogs it opens on demand.

## 3. Module map

| File | Responsibility |
|---|---|
| `mutagenmon.pyw` | Entry point. Loads config, installs a global exception hook that logs and shows an error dialog, creates the hidden `wx.Frame` and the `TaskBarIcon`, runs the wx main loop. |
| `mutagenmonlib/wx/icon.py` | **Core of the application.** `TaskBarIcon` class: tray icon state machine, 1-second UI timer, context menu, status dialog, notifications, self-restart logic. |
| `mutagenmonlib/wx/wx.py` | Small generic wx helpers: menu item factory, transient info dialog, message/error dialog helpers. |
| `mutagenmonlib/remote/monitor.py` | `Monitor` background thread: polls mutagen status on a fixed interval, maintains thread-safe session state (status, error counters, codes, conflicts, log), decides when a session must be restarted, runs conflict auto-resolution. |
| `mutagenmonlib/remote/mutagen.py` | Wraps the `mutagen` CLI (`sync list`, `sync create`, `sync terminate`), parses its text output into structured per-session status and conflict records, loads session definitions from the `.bat` file. |
| `mutagenmonlib/remote/resolve.py` | Manual conflict-resolution dialogs and logic: single-choice dialog (Visual merge / A wins / B wins), visual merge workflow, "too many conflicts" guard, batch resolution loop. |
| `mutagenmonlib/remote/ssh.py` | SSH/SCP helpers used when a session endpoint is remote (copy files, run remote `stat` to get size/mtime). |
| `mutagenmonlib/local/file.py` | Config loading (JSON with comments), path helpers, log file writers, config accessor `cfg()`. |
| `mutagenmonlib/local/lib.py` | Formatting utilities (timestamps, dict/status pretty-printing, matching-parenthesis parser used to parse mutagen's nested conflict output). |
| `mutagenmonlib/local/run.py` | `subprocess` wrapper with unified error logging/dialog and a `run_merge` helper for the external diff tool. |
| `config/config_mutagenmon.json` | All runtime configuration: paths to external binaries, polling/lag thresholds, notification toggles, auto-resolve rules. |
| `mutagen/mutagen-create.bat` | Defines the sessions to monitor (one `mutagen sync create --name=... ...` line per session). Also the source parsed to build the in-memory session list. |
| `img/*.png` | Tray icon bitmaps, one per status/state (see [03-tray-icon-requirements.md](03-tray-icon-requirements.md)). |
| `log/*.log` | `error.log`, `restart.log`, `resolve.log`, `debug.log` (debug log gated by `DEBUG_LEVEL`). |

## 4. Runtime architecture

```
┌────────────────────────────┐        1s wx.Timer        ┌───────────────────────────┐
│        wx App (UI thread)  │ ─────────────────────────▶ │      TaskBarIcon.update() │
│  hidden wx.Frame            │                            │  - reads Monitor state    │
│  TaskBarIcon (tray icon)    │◀──── click / menu ────────│  - recomputes worst code  │
└────────────────────────────┘                            │  - sets icon + tooltip    │
             ▲                                             │  - shows notifications   │
             │ modal dialogs (status, conflicts, errors)   └───────────────────────────┘
             │
┌────────────┴───────────────┐   poll every                ┌───────────────────────┐
│   Monitor (background      │   MUTAGEN_POLL_PERIOD ms    │   mutagen CLI process │
│   thread, thread-safe      │ ───────────────────────────▶│  `mutagen sync list`  │
│   getters/setters)         │◀─────────────────────────── │  `sync create/terminate`│
└─────────────────────────────┘   parsed text status        └───────────────────────┘
```

Two independent loops run concurrently:

1. **`Monitor` thread** (`remote/monitor.py`): every `MUTAGEN_POLL_PERIOD`
   (default 1000 ms) it shells out to `mutagen sync list`, parses the
   output into a per-session status dictionary and a conflicts list,
   updates a numeric "session code" per session, decides whether any
   session must be restarted (too many connect errors, missing session,
   duplicate session), stops sessions when the user disabled monitoring,
   and runs automatic conflict resolution.
2. **wx UI timer** (`wx/icon.py`, `TaskBarIcon.update`, every 1000 ms):
   reads the `Monitor`'s published state (never touches the mutagen CLI
   directly), computes the *worst* status across all sessions, updates the
   tray icon bitmap + tooltip accordingly, drains a message queue to show
   OS notifications, and checks staleness of the monitor's last update to
   decide whether to force a full application restart.

All cross-thread state is exchanged through `Monitor`'s lock-guarded
getters/setters and a `queue.Queue` of notification messages — there is no
shared mutable state accessed without synchronization.

## 5. Self-healing behavior

The application is designed to run unattended for long periods, so it
contains several automatic recovery mechanisms:

- **Per-session restart**: if a session stays in a "connecting" state past
  `SESSION_MAX_ERRORS` polls, has no session found past
  `SESSION_MAX_NOSESSION` polls, or is duplicated past
  `SESSION_MAX_DUPLICATE` polls, `Monitor` terminates and recreates it.
- **Whole-app restart**: if the tray icon fails to install
  (`IsIconInstalled()` becomes false) or the `Monitor`'s last successful
  status read is older than `STATUS_MAX_LAG.Restart` seconds (default 90s),
  the app logs the cause, spawns a new `mutagenmon` process, and exits.
- **Manual restart**: the tray menu's "Reload config && restart mutagen"
  disables all sessions and, once every session confirms it stopped,
  triggers the same whole-app restart path.

## 6. Known implementation quirks worth resolving in the rewrite

These are observed directly in the source and should be *consciously*
decided (kept, fixed, or intentionally dropped) when redesigning for the
WPF rewrite — see [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §6
for the detailed list. Highlights:

- Three icon assets referenced by `icon.py` do not exist in `img/`:
  `green-timeout-red.png`, `green-timeout-white.png`, `green-scan.png`.
  The app would raise/exhibit a broken icon in those states today.
- When a session is in the "ready" (worst code 100) state *and* its
  archive/profile just changed, no branch of `update_icon()` matches
  (the outer condition explicitly excludes `updated_profile`, and no
  `elif` re-tests code 100), so the "just updated" icon
  (`green-success.png`) is effectively unreachable for that case, even
  though the project's README describes a "files updated" icon shown for
  one second in that situation.
- The "stale" tooltip text is always the generic
  `"mutagen is watching for changes (stale)"` regardless of the actual
  worst status (conflicts/problems/syncing/scanning) — a user seeing a
  stale-but-conflicted session gets a misleading tooltip.
- Session names must be globally unique (enforced only by a warning dialog
  at startup, not a hard validation error) and only local/SSH transports
  are supported (explicit project limitation).
