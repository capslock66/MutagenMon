# Functional Requirements

Each requirement is derived directly from the current wxPython implementation
behavior, so it can serve as an executable specification for the WPF
rewrite. Requirement IDs are stable identifiers to be referenced from
code, tests, and PRs.

## FR-1 — Session configuration loading

- FR-1.1: The application MUST load a list of synchronization sessions from
  a session-definition source (currently: lines matching
  `mutagen sync create ... --name=<name> ...` in
  `mutagen/mutagen-create.bat`, skipping lines starting with `rem `).
- FR-1.2: Session names MUST be unique. If a duplicate name is found, the
  application MUST warn the user (currently: a modal dialog at startup) and
  MUST keep only one definition for that name.
- FR-1.3: The application MUST load a JSON configuration file that controls
  all runtime behavior described in this document (polling period, lag
  thresholds, notification toggles, external tool paths, auto-resolve
  rules). The format MUST tolerate `#`-prefixed comment lines.

## FR-2 — Continuous session status polling

- FR-2.1: A background process/task MUST poll the synchronization engine's
  status for all configured sessions on a fixed interval
  (`MUTAGEN_POLL_PERIOD`, default 1000 ms), independent of the UI thread.
- FR-2.2: For each session, the poll result MUST be parsed into at least:
  status text (e.g. "Watching for changes", "Scanning files", "Reconciling
  changes", "Staging files on ...", "Applying changes", "Saving archive",
  "Connecting to ...", "Waiting to connect", "Unknown"), a numeric session
  code (see FR-3), duplicate-name flag, problems flag, conflicts flag,
  session identifier, and per-endpoint URL/transport (local vs. SSH).
- FR-2.3: The full raw status text and the timestamp of the last successful
  poll MUST be retained for display and for staleness detection (FR-6).

## FR-3 — Session status classification (numeric code)

Each session MUST be classified into exactly one of the following codes on
every poll, using this precedence (a session in "connecting" state for too
long, or duplicated, degrades to an error code regardless of other flags):

| Code | Meaning | Trigger |
|---|---|---|
| `100` | Ready / watching for changes | status starts with "Watching for changes" |
| `40` | Syncing | status starts with "Waiting 5 seconds for rescan", "Reconciling changes", "Staging files on", "Applying changes", or "Saving archive" |
| `30` | Scanning | status starts with "Scanning files" |
| `-1` | No session / not running | no status reported for 2+ consecutive polls |
| `-2` | Connecting / cannot connect | status starts with "Connecting to", "Waiting to connect", or "Unknown" for 2+ consecutive polls, OR session is a duplicate for 2+ consecutive polls |
| `0` | Unknown / waiting for first status | initial value, before the first poll completes |

Additional downgrades applied after the base code is computed:

- If the session reports **problems**, its code MUST be capped at `50`
  (i.e. lowered to 50 if it was higher).
- If the session reports **conflicts**, its code MUST be capped at `60`.

## FR-4 — Aggregated ("worst") status

- FR-4.1: The application MUST compute a single aggregated status equal to
  the **minimum** numeric code across all configured sessions ("worst
  session wins").
- FR-4.2: This aggregated status MUST drive the tray icon and its tooltip
  (see [03-tray-icon-requirements.md](03-tray-icon-requirements.md) — the
  single most important requirement of the whole application).

## FR-5 — Tray icon (see dedicated document)

The full specification lives in
[03-tray-icon-requirements.md](03-tray-icon-requirements.md). Summary:

- FR-5.1: A persistent tray/notification-area icon MUST always be visible
  while the application runs, reflecting the aggregated status in
  real time (within one UI tick, i.e. ~1 second).
- FR-5.2: The icon's tooltip MUST contain a short, human-readable
  description of the current aggregated status.
- FR-5.3: Left-clicking (primary action) the icon MUST open a detailed
  status view (FR-8).
- FR-5.4: Right-clicking (secondary action) the icon MUST open a context
  menu (FR-7).
- FR-5.5: If the aggregated status has been stale for longer than a
  configurable threshold, the icon MUST visually communicate degraded
  confidence in the displayed status (a distinct "stale" visual state).

## FR-6 — Staleness detection & self-restart

- FR-6.1: The application MUST track how long ago the background polling
  last produced a result.
- FR-6.2: If that age exceeds configurable thresholds (`STATUS_MAX_LAG`:
  `Info`, `Warning`, `Error`, `Restart`, default 4/15/50/90 seconds), the
  UI MUST progressively degrade the icon's visual state (Info → subtle,
  Warning → visible, Error → strong) even though the underlying data has
  not changed.
- FR-6.3: If the age exceeds the `Restart` threshold, the application MUST
  log the cause and restart itself completely (spawn a new process
  instance and exit the current one).
- FR-6.4: If the tray icon itself fails to be installed/rendered by the OS,
  the application MUST treat this the same as FR-6.3 (self-restart).

## FR-7 — Tray context menu & session control

- FR-7.1: The context menu MUST offer "Reload config & restart mutagen",
  which stops all sessions, waits for confirmation they are stopped, then
  triggers a full application restart (FR-6.3 path).
- FR-7.2: The context menu MUST offer a toggle action: "Stop Mutagen
  sessions" when monitoring is currently enabled, or "Start Mutagen
  sessions" when it is currently disabled.
  - Disabling MUST stop (terminate) every running session.
  - Enabling MUST resume monitoring (sessions that are missing/stopped are
    (re)started by the normal restart logic, FR-9).
- FR-7.3: The context menu MUST offer "Show status", equivalent to a
  left-click (FR-8).
- FR-7.4: The context menu MUST offer "Exit MutagenMon", which stops the
  background poller and closes the application (no self-restart).
- FR-7.5: While a restart is in progress, the menu MUST replace the
  start/stop/reload/show-status items with a single disabled
  "Restarting..." item, keeping only "Exit" available.

## FR-8 — Detailed status view

- FR-8.1: On demand (left-click or menu), the application MUST show a
  window/dialog with the full status of every configured session: raw
  status text per session, with identifiers and endpoint URLs stripped for
  readability, and, if any un-auto-resolved conflicts exist, a list of
  `<session>: <file>` entries (annotated `[autoresolving]` for conflicts
  that will be resolved automatically) under a clearly separated
  "CONFLICTS" section.
- FR-8.2: If there are unresolved conflicts, the view MUST offer an action
  to start the conflict-resolution workflow (FR-9) in addition to
  dismissing the view.
- FR-8.3: If there are no unresolved conflicts, the view MUST be purely
  informational (single dismiss action).

## FR-9 — Manual conflict resolution

- FR-9.1: For every unresolved conflict, one at a time (numbered "N of
  total"), the application MUST present: the conflicting file path, and
  for each side (A/"alpha" and B/"beta") the endpoint URL, file size, and
  last-modified timestamp (fetched locally or via SSH `stat` for remote
  endpoints).
- FR-9.2: The user MUST be able to choose one of: **Visual merge** (opens
  an external diff/merge tool on local copies of both files, then, if the
  local "A" copy was modified by the tool, propagates the merged result to
  both sides and confirms via a message), **A wins** (copy A's version
  over B), or **B wins** (copy B's version over A).
- FR-9.3: The dialog MUST pre-select "A wins" or "B wins" automatically
  based on which side has the more recent modification timestamp, as a
  suggested default the user can override.
- FR-9.4: Cancelling MUST stop the whole batch resolution workflow
  immediately (no further conflicts are presented).
- FR-9.5: If more than 100 unresolved conflicts are pending, the
  application MUST refuse to start the batch workflow and inform the user
  they should resolve conflicts manually or restart the process.
- FR-9.6: While copying/inspecting a remote (SSH) endpoint, the application
  MUST show a lightweight "connecting" indicator, dismissed automatically
  once the operation completes.
- FR-9.7: Every resolution (manual or automatic) MUST be appended to a
  resolution log with session, both URLs, filename, method, and whether it
  was automatic.

## FR-10 — Automatic conflict resolution

- FR-10.1: The application MUST support a configurable, ordered list of
  rules, each pairing a regular expression matched against the conflicting
  file's path and a resolution ("A wins" / "B wins").
- FR-10.2: On every poll, each newly-seen conflict MUST be checked against
  the rules in order; the first match MUST be applied automatically
  (no user interaction) and the conflict MUST be flagged `autoresolved` so
  it is excluded from the manual workflow (FR-9) and from the "new
  conflict" notification (FR-11).
- FR-10.3: Once a conflict (identified by session + filename) has been
  auto-resolved, it MUST NOT be reprocessed for a configurable grace period
  (`AUTORESOLVE_HISTORY_AGE`, default 30s), to avoid a loop if the
  underlying tool re-reports the same conflict before the sync engine
  catches up.
- FR-10.4: If notifications for auto-resolve are enabled, each
  auto-resolution MUST raise a notification (FR-11) naming the rule applied
  and the file.

## FR-11 — Desktop notifications

The application MUST be able to raise OS-level notifications (balloon /
toast, non-modal, auto-dismissing) for, each independently toggleable via
configuration:

- FR-11.1: **New conflicts** detected since the last check (grouped, one
  notification listing all newly seen `session:file` conflict keys).
- FR-11.2: **Automatic conflict resolution** performed (FR-10.4).
- FR-11.3: **Session restarted due to a stuck connection** (i.e. the
  connecting-state error-count threshold was hit).
- FR-11.4: **Mutagen session profile/archive updated** on disk (i.e. a
  session's underlying sync archive changed), per session name.

## FR-12 — Session profile change detection

- FR-12.1: On a configurable interval (`MUTAGEN_PROFILE_DIR_WATCH_PERIOD`),
  for every enabled session, the application MUST watch the modification
  time of the sync engine's on-disk archive file for that session.
- FR-12.2: A change MUST be debounced by a grace period
  (`MUTAGEN_PROFILE_GRACE`) before being reported as a real update, to
  avoid reacting to rapid successive writes.
- FR-12.3: A confirmed update MUST be exposed to both the tray icon logic
  (as an "updated" visual variant, see tray icon spec) and the notification
  system (FR-11.4).

## FR-13 — Automatic session recovery

- FR-13.1: If a session shows no result for more than
  `SESSION_MAX_NOSESSION` consecutive polls, it MUST be restarted
  (terminate + recreate).
- FR-13.2: If a session is detected as a duplicate for more than
  `SESSION_MAX_DUPLICATE` consecutive polls, it MUST be restarted.
- FR-13.3: If a session stays in a "connecting" state for more than
  `SESSION_MAX_ERRORS` consecutive polls, it MUST be restarted.
- FR-13.4: Every automatic restart MUST be appended to a restart log
  together with the raw status snapshot that triggered it.

## FR-14 — Logging & diagnostics

- FR-14.1: Unhandled exceptions MUST be logged with full traceback to an
  error log file, and, unless a "log to console instead" debug flag is
  set, MUST be shown to the user in a blocking error dialog.
- FR-14.2: A configurable verbosity level MUST gate a separate debug log
  capturing internal state transitions (0 = disabled, up to 100 = maximum
  verbosity).
- FR-14.3: Restarts (FR-13) and conflict resolutions (FR-9/FR-10) MUST be
  logged to their own dedicated log files, independent of the debug log.

### Rewrite implementation note (deliberate simplification)

FR-14.1–14.3 above describe the legacy behavior (4 separate files:
`error.log`, `debug.log` gated by `DEBUG_LEVEL`, `restart.log`,
`resolve.log`). The .NET rewrite deliberately simplifies this rather than
reproducing it verbatim — see
[05-wpf-migration-notes.md §7](05-wpf-migration-notes.md#7-logging)
for the rationale:

- **FR-14.1 (implemented, Phase 1)**: satisfied — every unhandled
  exception (startup, UI thread, background threads, unobserved task
  exceptions) is logged with full exception detail and always shown to the
  user via a blocking `MessageBox`. The legacy's "log to console instead"
  flag (`DEBUG_EXCEPTIONS_TO_CONSOLE` in config) is preserved as a config
  key for compatibility but has no effect yet in the rewrite.
- **FR-14.2 (deliberately not reproduced)**: the rewrite uses a single
  always-on log sink capturing every level (Debug and above), all the
  time — no verbosity gate, no separate debug file. `DEBUG_LEVEL` remains
  in `config_mutagenmon.json` for compatibility but currently has no
  effect. Rationale: the legacy's default-off debug log was the direct
  cause of a real diagnosability incident during Phase 1 manual
  verification (a startup exception produced literally no log output,
  because logging hadn't even been configured yet at the point it was
  thrown) — always-on beats "remember to flip a flag after the fact."
- **FR-14.3 (deferred)**: not yet implemented. Dedicated restart/resolve
  log files depend on FR-13 (automatic session restart execution) and
  FR-9/FR-10 (conflict resolution), none of which are built yet (Phase
  3/5 per the migration notes' phased plan). Until then, the one
  self-restart mechanism that *is* implemented in Phase 1 (the tray
  icon's staleness watchdog, FR-6) logs to the same single file as
  everything else.

## FR-15 — Single, always-on background operation

- FR-15.1: The application is intended to run continuously
  (e.g. from OS startup) with no persistent window other than the tray
  icon; the main "window" (if any) MUST never be shown to the user in
  normal operation.
- FR-15.2: Graceful termination signals (SIGINT/SIGTERM) MUST result in a
  clean shutdown (stop background polling, remove tray icon) rather than
  an abrupt kill.
