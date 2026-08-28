# Functional Requirements

Each requirement is derived directly from the legacy implementation's
behavior (its source has since been removed from this repository), so it
can serve as an executable specification for the WPF rewrite. Requirement
IDs are stable identifiers to be referenced from code, tests, and PRs.

## FR-1 — Session configuration loading

- FR-1.1: The application MUST load a list of synchronization sessions from
  a session-definition source: every line of
  `mutagen/mutagen-create.bat` (path configurable, see
  `MUTAGEN_SESSIONS_BAT_FILE` in
  [06-configuration-reference.md](06-configuration-reference.md)) that does
  NOT start with `rem `, and from which a session name can be extracted
  with the pattern `--name=(.*?) ` (a **lazy** match up to the next literal
  space) — i.e. the substring between `--name=` and the following space
  becomes the session name, and a line is silently skipped (contributes no
  session) if `--name=` is missing or is not followed by a later space on
  the same line. The **entire matched line** (not just the name) is kept
  as that session's start command, later reused verbatim to (re)create it
  (FR-13.5).
- FR-1.2: Session names MUST be unique. If a duplicate name is found, the
  application MUST warn the user with a blocking, OK-only, informational
  modal dialog at startup (title `"MutagenMon"`, body
  `"<name> session name is duplicate in <MUTAGEN_SESSIONS_BAT_FILE path>"`),
  one dialog per duplicate encountered, and MUST keep only the
  **last**-seen definition for that name (each new line for the same name
  overwrites the previous one).
- FR-1.3: The application MUST load a JSON configuration file that controls
  all runtime behavior described in this document (polling period, lag
  thresholds, notification toggles, external tool paths, auto-resolve
  rules). The format MUST tolerate `#`-prefixed comment lines. See
  [06-configuration-reference.md](06-configuration-reference.md) for every
  key, its type, default value, and unit.

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
| `-1` | No session / not running | no status reported for 3 consecutive polls in a row (2, if this is the very first poll ever for that session — see note below) |
| `-2` | Connecting / cannot connect | status starts with "Connecting to", "Waiting to connect", or "Unknown" for 3 consecutive polls in a row, OR session is a duplicate for 3 consecutive polls in a row |
| `0` | Unknown / waiting for first status | initial value, before the first poll completes |

The "3 consecutive polls" figure follows from the same
consecutive-abnormal-poll counter defined in
[FR-13](#fr-13--automatic-session-recovery): the counter is `0` on the
first poll where the abnormal condition appears, `1` on the second, `2`
on the third — and the code changes to `-1`/`-2` as soon as the counter is
`> 1`, i.e. on the third poll. The one exception is "no session": a
session's initial "last known status" is the empty string, which is also
the internal marker used for "no session" — so a session that has *never*
reported any status reaches code `-1` after only 2 polls, not 3, purely
because of this initial-value coincidence, not because the rule is
different.

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

- FR-9.1: For every unresolved conflict, one at a time (dialog title
  `"MutagenMon: resolve file conflict <N> of <total>"`), the application
  MUST present: the conflicting file path, and for each side
  (A/"alpha" and B/"beta") the endpoint URL, file size, and last-modified
  timestamp (fetched locally or via SSH `stat` for remote endpoints), laid
  out as `"A: <url1>\n<size> bytes, <timestamp>"` (and likewise for B).
  > **Legacy quirk, not to reproduce silently**: in the current
  > implementation, `<total>` is actually the **number of configured
  > sessions**, not the number of conflicts to resolve (it is computed as
  > `len(conflicts_dict)`, and that dict always has exactly one entry per
  > configured session, empty or not). With 3 sessions and 7 real
  > conflicts, the dialog cycles "1 of 3", "2 of 3", ... "7 of 3" — `<N>`
  > legitimately exceeds `<total>`. The rewrite SHOULD instead compute
  > `<total>` as the real count of unresolved (non-autoresolved) conflicts
  > across all sessions, unless legacy-parity is explicitly requested.
- FR-9.2: The user MUST be able to choose one of: **Visual merge** (opens
  an external diff/merge tool, `MERGE_PATH`, on local copies of both files;
  if the local "A" copy's file was modified afterwards — mtime changed —
  the merged result is copied to **both** sides, and a confirmation dialog
  is shown: title `"MutagenMon: resolved file conflict"`, body
  `"Merged file copied to both sides:\n\n<filename>"`), **A wins** (copy
  A's version over B), or **B wins** (copy B's version over A).
  > **Directory-level conflicts**: mutagen can also report a conflict where
  > one or both sides are a whole directory rather than a single file (e.g.
  > `(alpha) some/dir (Directory -> <non-existent>)`), typically from
  > deleting a synced subdirectory on one side while new untracked content
  > appears under it on the other. The dialog handles this by showing
  > `(directory)`/`(does not exist)` instead of a byte size where
  > applicable, and disabling **Visual merge** (not meaningful for a
  > directory). **A wins**/**B wins** mirrors the winning side's subtree
  > onto the other side recursively, or deletes the destination entirely
  > when the winning side has nothing at that path.
- FR-9.3: The dialog MUST pre-select "A wins" or "B wins" automatically
  based on which side has the more recent modification timestamp, as a
  suggested default the user can override.
- FR-9.4: Cancelling the dialog for one conflict MUST **skip only that
  conflict** and immediately present the next unresolved conflict in the
  batch — it MUST NOT abort the whole batch. (Correction to a
  misdescription in earlier revisions of this document: the legacy
  `resolve_single()` treats Cancel identically to a completed resolution —
  both make the outer loop move on to the next conflict — there is no code
  path that stops the batch early other than exhausting all conflicts or
  hitting the FR-9.5 cap.) The only way to leave a conflict *unresolved and
  revisit it later* is to close/kill the whole application before the
  batch completes.
  > **⚠ Implementation discrepancy found while correcting this
  > requirement**: the current .NET implementation
  > (`ConflictResolutionController.cs`,
  > [dotNet/src/MutagenMon.App](../dotNet/src/MutagenMon.App)) deliberately
  > aborts the whole batch on Cancel, citing FR-9.4 in its own doc comment
  > — i.e. it was built against this document's *previous, incorrect*
  > wording, not against the legacy app's actual behavior. This is a real
  > behavioral divergence from legacy parity that needs an explicit
  > decision (fix the code to match this corrected FR-9.4, or knowingly
  > keep abort-on-cancel as a deliberate rewrite-only improvement and say
  > so here) — it has not been fixed as part of this documentation pass.
- FR-9.5: If more than 100 unresolved (non-autoresolved) conflicts are
  pending, the application MUST stop presenting further conflicts (the
  first 100 in iteration order are still resolved one by one as normal)
  and show a blocking, OK-only, informational dialog — title
  `"MutagenMon: resolve file conflict"`, body
  `"Too many conflicts. You can restart resolving or resolve manually"`
  — then abandon the rest of the batch (the remaining conflicts are left
  unresolved until the next time the workflow is invoked).
- FR-9.6: While copying/inspecting a remote (SSH) endpoint, the application
  MUST show a lightweight "connecting" indicator, dismissed automatically
  once the operation completes.
- FR-9.7: Every resolution (manual or automatic) MUST be appended to a
  resolution log with session, both URLs, filename, method, and whether it
  was automatic.

## FR-10 — Automatic conflict resolution

- FR-10.1: The application MUST support a configurable, ordered list of
  rules (`AUTORESOLVE`, default: empty list — no rule pre-configured), each
  pairing a regular expression matched against the conflicting file's path
  and a resolution (the literal string `"A wins"` or `"B wins"`). The match
  MUST be an unanchored substring search against the full path (directory +
  filename), not a whole-string match — e.g. a pattern of `nohup\.out$`
  matches any path ending in `nohup.out`, regardless of its directory. See
  [06-configuration-reference.md](06-configuration-reference.md) for the
  exact schema and example rules.
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
toast, non-modal, auto-dismissing) for the following events. Each is
independently toggleable via configuration **except where noted** — the
three FR-13 restart causes deliberately do NOT share one toggle:

- FR-11.1: **New conflicts** detected since the last check (grouped, one
  notification listing all newly seen `session:file` conflict keys).
  Toggle: `NOTIFY_CONFLICTS` (default `true`).
- FR-11.2: **Automatic conflict resolution** performed (FR-10.4).
  Toggle: `NOTIFY_AUTORESOLVE` (default `true`).
- FR-11.3: **Session restarted because it was stuck in "connecting"**
  (FR-13.3 threshold hit). Toggle: `NOTIFY_RESTART_CONNECTION` (default
  `false`, i.e. this notification is **off by default**).
- FR-11.3b: **Session restarted because it was detected as a duplicate**
  (FR-13.2 threshold hit). This notification is **always raised, with no
  configuration toggle** — it MUST NOT be gated by
  `NOTIFY_RESTART_CONNECTION` or any other flag.
- FR-11.3c: **Session restarted because no session was found**
  (FR-13.1 threshold hit) does **NOT** raise a notification at all, by
  design — this restart cause is silent.
- FR-11.4: **Mutagen session profile/archive updated** on disk (i.e. a
  session's underlying sync archive changed), per session name.
  Toggle: `NOTIFY_MUTAGEN_PROFILE_UPDATE` (default `false`).

See [06-configuration-reference.md](06-configuration-reference.md) for the
full default-value table.

## FR-12 — Session profile change detection

- FR-12.1: On a configurable interval (`MUTAGEN_PROFILE_DIR_WATCH_PERIOD`,
  default `1` second, `0` disables the watch entirely), for every enabled
  session, the application MUST watch the modification time of the sync
  engine's on-disk archive file for that session
  (`<MUTAGEN_PROFILE_DIR>/archives/<session-id>`, default
  `%USERPROFILE%\.mutagen\archives\<session-id>` — the sync engine's own
  data directory, not part of this application's config/log paths). If the
  archive file cannot be found (e.g. session not yet created), the watch
  MUST be silently reset (no error) so the first future appearance of the
  file is not immediately reported as an "update".
- FR-12.2: A change MUST be debounced by a grace period
  (`MUTAGEN_PROFILE_GRACE`, default `4` seconds) before being reported as a
  real update: a new modification is only confirmed once at least the
  grace period has elapsed since the *previously confirmed* modification —
  to avoid reacting to rapid successive writes.
- FR-12.3: A confirmed update MUST be exposed to both the tray icon logic
  (as an "updated" visual variant, see tray icon spec) and the notification
  system (FR-11.4), which is itself independently toggled
  (`NOTIFY_MUTAGEN_PROFILE_UPDATE`, default `false`) — a confirmed update
  can drive the icon without ever producing a notification.

## FR-13 — Automatic session recovery

Each session has a **consecutive-abnormal-poll counter**, reset to `0` on
the poll where an abnormal (status, duplicate-flag) pair first appears
(even coming from a *different* abnormal pair), then incremented by 1 on
every subsequent poll where that same pair repeats unchanged; any change
back to a healthy pair also resets it to `0`. A restart threshold below is
crossed once this counter exceeds (strictly `>`) the configured value —
concretely, the abnormal condition must be observed for `configured value
+ 2` consecutive polls in a row (poll 1 sets the counter to `0`, poll 2
brings it to `1`, ..., poll `configured value + 2` brings it to
`configured value + 1`, which is the first value `> configured value`).
See [06-configuration-reference.md](06-configuration-reference.md) for
exact default values and the approximate wall-clock time they represent at
the default 1000 ms poll period (the `+2` polls are negligible at that
scale and are folded into the "≈" approximations there).

- FR-13.1: If a session shows no result at all for more than
  `SESSION_MAX_NOSESSION` consecutive polls (default `200`, ≈3 min 20 s),
  it MUST be restarted (terminate + recreate, FR-13.5). No notification is
  raised for this cause (FR-11.3c).
- FR-13.2: If a session is detected as a duplicate name for more than
  `SESSION_MAX_DUPLICATE` consecutive polls (default `10000`, ≈2 h 47 min),
  it MUST be restarted. A notification is **always** raised for this cause,
  unconditionally (FR-11.3b).
- FR-13.3: If a session stays in a "connecting" state (status starting with
  "Connecting to", "Waiting to connect", or "Unknown" — see FR-3) for more
  than `SESSION_MAX_ERRORS` consecutive polls (default `30000`, ≈8 h 20
  min), it MUST be restarted. A notification is raised only if
  `NOTIFY_RESTART_CONNECTION` is enabled (default `false`) — see FR-11.3.
- FR-13.4: Every automatic restart, of any of the three causes above, MUST
  be appended to a restart log together with the raw status snapshot that
  triggered it and which of the three causes fired.
- FR-13.5: The restart action itself is: request termination of the
  session (`mutagen sync terminate <name>`), then recreate it from its
  original definition (FR-1.1). Both steps MUST tolerate failure of the
  other (e.g. terminate failing because the session was already gone MUST
  NOT prevent the recreate attempt). After a restart, the session's
  consecutive-abnormal-poll counter MUST be reset to `0` immediately,
  independent of the next poll's outcome.
  - **Rewrite-only refinement**: when the restart cause is FR-13.1 (no
    session at all), the termination step MUST be skipped entirely rather
    than attempted and tolerated — the session is already known to be
    absent, so `mutagen sync terminate` would be a guaranteed-to-fail,
    purely noisy call. For the other two causes (FR-13.2 duplicate,
    FR-13.3 stuck-connecting), the session is known to exist, so
    termination MUST still be requested and its failure MUST still be
    tolerated as described above. The legacy app always attempted
    termination unconditionally; this refinement is a deliberate .NET-only
    improvement, not a legacy-parity requirement.
- FR-13.6: Automatic restarts (this section) only run while monitoring is
  currently enabled (FR-7.2); while disabled, no session is restarted, and
  any currently-running session MUST instead be stopped (terminated,
  not recreated) on the next poll.

## FR-14 — Logging & diagnostics

> **FR-14.1–14.3 below describe the legacy behavior only.** The
> .NET rewrite deliberately does **not** reproduce the separate-file
> layout — see the "Rewrite implementation note" at the end of this
> section, in particular the FR-14.1 bullet, for what the .NET app
> actually writes and where.

- FR-14.1: Unhandled exceptions MUST be logged with full traceback to an
  error log file (`<LOG_PATH>/error.log`), and, unless
  `DEBUG_EXCEPTIONS_TO_CONSOLE` is `true` (default `false`), MUST be shown
  to the user in a blocking, OK-only error dialog with title
  `"MutagenMon error"` and the traceback as body text. This applies
  uniformly to: external-process failures (`mutagen`/merge tool
  non-zero exit or launch failure), the tray icon failing to (re)install
  (FR-6.4), and any other unhandled exception reaching the top of the main
  loop.
- FR-14.2: A configurable verbosity level (`DEBUG_LEVEL`, default `0`) MUST
  gate a separate debug log (`<LOG_PATH>/debug.log`) capturing internal
  state transitions (0 = disabled, up to 100 = maximum verbosity) — each
  logged line carries its own verbosity level, and only lines at or below
  the configured `DEBUG_LEVEL` are written.
- FR-14.3: Restarts (FR-13) and conflict resolutions (FR-9/FR-10) MUST be
  logged to their own dedicated log files, independent of the debug log.

### Rewrite implementation note (deliberate simplification)

FR-14.1–14.3 above describe the legacy behavior (4 separate files:
`error.log`, `debug.log` gated by `DEBUG_LEVEL`, `restart.log`,
`resolve.log`). The .NET rewrite deliberately simplifies this rather than
reproducing it verbatim:

- **FR-14.1 (implemented, Phase 1)**: satisfied, but **not** via a
  dedicated `error.log` — there is no such file in the .NET app.
  Exceptions are logged, at `Information` level or above, to the same
  single unified log file as everything else (FR-14.2's rewrite note
  below): `<LOG_PATH>/mutagenMon.log` by default, written through a
  hand-rolled `ILoggerProvider` (`FileLoggerProvider`, see
  [App.xaml.cs](../dotNet/src/MutagenMon.App/App.xaml.cs)). A second,
  genuinely separate file, `mutagenMon.fatal.log` (next to the executable,
  not under `LOG_PATH`), exists purely as a fallback for failures that
  happen before `mutagenMon.log`'s own path can be resolved from config,
  or if writing to it fails — it stays empty in normal operation and is
  not where you should look for ordinary exceptions. Every unhandled
  exception (startup, UI thread, background threads, unobserved task
  exceptions, including ones raised inside a nested dispatcher frame such
  as an open context menu — a known WPF gotcha the rewrite specifically
  guards against) is logged with full exception detail and always shown
  to the user via a blocking `MessageBox`. The legacy's "log to console
  instead" flag (`DEBUG_EXCEPTIONS_TO_CONSOLE` in config) is preserved as
  a config key for compatibility but has no effect yet in the rewrite.
- **FR-14.2 (deliberately not reproduced)**: the rewrite uses a single
  always-on log sink capturing every level (Debug and above), all the
  time — no verbosity gate, no separate debug file. `DEBUG_LEVEL` remains
  in `config_mutagenmon.json` for compatibility but currently has no
  effect. Rationale: the legacy's default-off debug log was the direct
  cause of a real diagnosability incident during Phase 1 manual
  verification (a startup exception produced literally no log output,
  because logging hadn't even been configured yet at the point it was
  thrown) — always-on beats "remember to flip a flag after the fact."
- **FR-14.3 (partially implemented, Phase 3)**: the conflict-resolution
  half is done — every manual resolution (FR-9) appends to a dedicated
  `resolve.log`, independent of the main log
  (`MutagenMon.Core/Resolution/ResolveLogWriter.cs`). The restart-log half
  still depends on FR-13 (automatic session restart execution, Phase 5),
  not yet built; until then, the one self-restart mechanism that *is*
  implemented in Phase 1 (the tray icon's staleness watchdog, FR-6) logs
  to the same single file as everything else.

## FR-15 — Single, always-on background operation

- FR-15.1: The application is intended to run continuously
  (e.g. from OS startup) with no persistent window other than the tray
  icon; the main "window" (if any) MUST never be shown to the user in
  normal operation.
- FR-15.2: Graceful termination signals (SIGINT/SIGTERM) MUST result in a
  clean shutdown (stop background polling, remove tray icon) rather than
  an abrupt kill.
