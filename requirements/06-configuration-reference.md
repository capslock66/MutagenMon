# Configuration Reference

This document is the single source of truth for every runtime configuration
key read from `config_mutagenmon.json`. It exists so that a developer
working from `requirements/` alone never needs to guess a default value, a
unit, or which requirement a key drives — the legacy source it was
originally derived from has since been removed from this repository.

The legacy file format tolerates `#`-prefixed comment lines (FR-1.3), which
are not valid JSON; they MUST be stripped before parsing. All numeric
"count" thresholds below are expressed in **consecutive polls** unless
stated otherwise, and all "period"/"age" values are in the unit given.

| Key | Type | Default | Unit | Description | Used by |
|---|---|---|---|---|---|
| `DEBUG_LEVEL` | integer | `0` | verbosity (0–100) | Gates the debug log; `0` disables it, `100` is maximum verbosity. | FR-14.2 |
| `DEBUG_EXCEPTIONS_TO_CONSOLE` | boolean | `false` | — | If `true`, unhandled exceptions are printed to the console instead of shown in a blocking error dialog. | FR-14.1 |
| `NOTIFY_RESTART_CONNECTION` | boolean | `false` | — | Enables the desktop notification when a session is restarted because it was stuck in "connecting" (FR-13.3). Does **not** gate the "duplicate" or "no session" restart cases — see FR-11.3 note below. | FR-11.3, FR-13.3 |
| `NOTIFY_CONFLICTS` | boolean | `true` | — | Enables the "new conflicts detected" notification. | FR-11.1 |
| `NOTIFY_AUTORESOLVE` | boolean | `true` | — | Enables the notification raised when a conflict is auto-resolved. | FR-10.4, FR-11.2 |
| `NOTIFY_MUTAGEN_PROFILE_UPDATE` | boolean | `false` | — | Enables the per-session notification when the sync archive changed on disk. | FR-11.4, FR-12.3 |
| `START_ENABLED` | boolean | `true` | — | If `true`, monitoring starts in the "enabled" state (auto-restart active) rather than paused. | FR-7.2, FR-15 |
| `MERGE_PATH` | string (path) | `C:\Program Files (x86)\WinMerge\WinMergeU` | — | Path to the external visual diff/merge tool executable. | FR-9.2 |
| `SCP_PATH` | string (path) | `C:\Program Files\Git\usr\bin\scp` | — | Path to the `scp` binary used for remote (SSH) file transfer during conflict resolution. | FR-9.1, FR-9.2 |
| `SSH_PATH` | string (path) | `C:\Program Files\Git\usr\bin\ssh` | — | Path to the `ssh` binary used for remote `stat` calls (file size/mtime). | FR-9.1 |
| `MUTAGEN_PATH` | string (path) | `mutagen\mutagen` | — | Path to the `mutagen` CLI executable, used for both polling (`sync list`) and session control (`sync create`/`sync terminate`). | FR-1.1, FR-2.1, FR-13 |
| `TRAY_TOOLTIP` | string | `"MutagenMon"` | — | Application name used as the prefix of every tray tooltip (`"<TRAY_TOOLTIP>: <state text>"`) and as the title of the status/error dialogs. | FR-5.2, TIC-5 |
| `LOG_PATH` | string (path) | `"log"` | — | Base directory for all log files (`error.log`, `debug.log`, `restart.log`, `resolve.log` in the legacy app — see FR-14 and its rewrite note). | FR-14 |
| `MUTAGEN_SESSIONS_BAT_FILE` | string (path) | `mutagen/mutagen-create.bat` | — | Path to the batch file containing one `mutagen sync create ... --name=<name> ...` line per session; lines starting with `rem ` are skipped. | FR-1.1 |
| `SESSION_MAX_ERRORS` | integer | `30000` | consecutive polls | Number of consecutive polls a session may stay in the "connecting" state (code `-2`, non-duplicate) before it is restarted. At the default 1000 ms poll period this is **~8 h 20 min**. | FR-13.3 |
| `SESSION_MAX_NOSESSION` | integer | `200` | consecutive polls | Number of consecutive polls a session may report no result at all before it is restarted. At the default poll period this is **~3 min 20 s**. | FR-13.1 |
| `SESSION_MAX_DUPLICATE` | integer | `10000` | consecutive polls | Number of consecutive polls a session may be flagged as a duplicate name before it is restarted. At the default poll period this is **~2 h 47 min**. | FR-13.2 |
| `MUTAGEN_POLL_PERIOD` | integer | `1000` | milliseconds | Interval between two `mutagen sync list` calls on the background polling thread/task. | FR-2.1 |
| `STATUS_MAX_LAG` | object `{Info, Warning, Error, Restart}` | `{"Info": 4, "Warning": 15, "Error": 50, "Restart": 90}` | seconds (per key) | Age of the last successful poll result, past which the tray icon degrades to the corresponding staleness tier (`Info`/`Warning`/`Error`), or, past `Restart`, the whole application self-restarts. | FR-6.2, FR-6.3, TIC-9/TIC-10 |
| `MUTAGEN_PROFILE_DIR` | string (path) | `%USERPROFILE%\.mutagen` | — | Root directory of the sync engine's own data, containing the per-session archive files watched for "updated" detection (`archives\<session-id>`). | FR-12.1 |
| `MUTAGEN_PROFILE_DIR_WATCH_PERIOD` | integer | `1` | seconds, or `0` to disable | Interval at which the archive-file modification time is re-checked. Implemented as a modulo on the 1 Hz UI tick counter (i.e. a value of `N` means "every Nth UI tick, not every N seconds of wall-clock drift"); `0` disables the watch entirely. | FR-12.1 |
| `MUTAGEN_PROFILE_GRACE` | integer | `4` | seconds | Debounce window: an archive modification is only reported as a confirmed "update" once at least this many seconds have passed since the previously confirmed update, to avoid reacting to rapid successive writes. | FR-12.2 |
| `AUTORESOLVE` | array of `{filepath, resolve}` | `[]` (4 example entries in the shipped sample config) | — | Ordered list of auto-resolve rules. `filepath` is a regular expression matched against the conflicting file's full path (directory + filename); `resolve` MUST be the literal string `"A wins"` or `"B wins"`. The first matching rule (in array order) wins. | FR-10.1, FR-10.2 |
| `AUTORESOLVE_HISTORY_AGE` | integer | `30` | seconds | Once a `(session, filename)` pair has been auto-resolved, it is not reprocessed for this long, to avoid a resolve loop while the sync engine catches up. | FR-10.3 |

## Notes for the rewrite

- **Consecutive-poll counters reset on any state change**, not just on
  recovery: the legacy implementation resets a session's error counter to
  `0` the instant its (status, duplicate-flag) pair differs from the
  previous poll's, even if the new state is still abnormal (e.g. flipping
  from "connecting" to "no session" resets the counter rather than adding
  the two counts together). Only a *run* of identical abnormal readings
  counts toward the threshold. See FR-13 for the reference algorithm
  (originally in the removed legacy app's polling loop).
- **Restart notification is inconsistent by design in the legacy app, not
  a bug to silently "fix" without a decision**: of the three FR-13 restart
  causes, only the "connecting" case (FR-13.3) is gated by
  `NOTIFY_RESTART_CONNECTION`; the "duplicate" case (FR-13.2) *always*
  raises a notification regardless of any config flag, and the "no
  session" case (FR-13.1) *never* raises one. See FR-11.3.
- `AUTORESOLVE`'s regular expressions are matched with unanchored
  substring-search semantics, not a full-path anchor — a
  pattern such as `nohup\.out$` matches anywhere in the path as long as it
  ends with `nohup.out`.
- No key in this file is ever expected to hold a secret (NFR-9); paths and
  thresholds only.
