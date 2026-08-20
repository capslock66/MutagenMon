# Non-Functional Requirements

## NFR-1 — Real-time responsiveness (highest priority)

- The tray icon MUST reflect a change in aggregated session status within
  one polling+UI cycle, i.e. **at most ~1–2 seconds** end to end under
  normal load (current defaults: 1000 ms background poll + 1000 ms UI
  tick). This is the single most important quality attribute of the
  application — see [03-tray-icon-requirements.md](03-tray-icon-requirements.md).
- Reading/refreshing the tray status MUST NOT block the OS shell or the
  process's own UI thread; polling the sync engine MUST happen off the UI
  thread.

## NFR-2 — Reliability & self-healing

- The application MUST be able to run unattended for weeks without manual
  intervention, recovering automatically from: a hung/crashed sync engine
  process, a stale internal status, and a failed tray icon registration
  (see FR-6, FR-13).
- A crash in the background monitoring logic MUST be logged with full
  context before the process exits (never a silent disappearance from the
  tray).

## NFR-3 — Resource footprint

- As a permanently-running background utility, CPU and memory usage MUST
  stay minimal at idle (current implementation: a 1-second timer plus a
  1-second poll, each doing lightweight text parsing of a CLI's output —
  no continuous rendering, no heavy in-memory history).
- The application MUST run as a single instance; it must not accumulate
  duplicate tray icons or duplicate background pollers across
  restarts.

## NFR-4 — Portability

- The original project targets Windows, Linux, and macOS, but has only
  been exercised on Windows; external tool paths (SSH, SCP, diff/merge)
  are OS-specific and must remain configurable per platform.
- The rewrite's primary target is Windows first-class (tray icon,
  notifications, external tool integration), with the architecture kept
  open to other desktop OSes where the chosen UI stack allows it.

## NFR-5 — Configurability

- All thresholds, toggles, and external tool paths MUST remain externally
  configurable without recompiling (current: a JSON file with inline
  comments). Sensible defaults MUST allow zero-config startup for the
  common case (local-only sessions, no auto-resolve rules).

## NFR-6 — Observability

- All automatic decisions that affect the user's data (session restarts,
  automatic conflict resolution) MUST be logged with enough context to be
  reconstructed after the fact (timestamp, session name, both endpoint
  URLs, action taken).
- A diagnostic log MUST be available for troubleshooting without needing a
  debugger attached. The legacy app gates this behind a verbosity level
  (`DEBUG_LEVEL`, off by default); the .NET rewrite deliberately drops that
  gate — a single log file always captures every level, so a launch
  failure or crash is never undiagnosable just because nobody had
  remembered to raise the verbosity beforehand (see
  [01-functional-requirements.md FR-14](01-functional-requirements.md#fr-14--logging--diagnostics)
  and
  [05-wpf-migration-notes.md §7](05-wpf-migration-notes.md#7-logging)).

## NFR-7 — Usability / minimal intrusiveness

- The application MUST stay out of the user's way by default: no modal
  window on startup, status conveyed passively through the tray icon and
  its tooltip, and interruption (dialogs, notifications) reserved for
  situations that need attention (conflicts, errors) or that the user
  explicitly requested (clicking the icon).
- Destructive/attention-worthy actions (batch conflict resolution,
  stopping sessions) MUST always be reachable from the tray icon within
  two interactions (click/right-click, then one menu choice).

## NFR-8 — Data safety during conflict resolution

- Any operation that overwrites a user's file as part of conflict
  resolution (FR-9, FR-10) MUST be logged before or immediately after the
  copy, and MUST clearly indicate which side "won", so an unwanted
  resolution can be identified and manually reverted using the log.
- Automatic conflict resolution MUST be strictly opt-in per path pattern
  (default: no rules configured, nothing is auto-resolved).

## NFR-9 — Security

- Credentials are never stored by the application; remote access relies on
  the ambient SSH configuration (keys/agent) of the OS user running the
  process.
- Remote file paths and hostnames used for SSH/SCP MUST be shell-escaped
  before being passed to external processes to avoid command injection via
  crafted file/directory names.
- Configuration files may contain local filesystem paths but no secrets by
  design; the rewrite MUST preserve this property (no credentials in
  config).

## NFR-10 — Maintainability

- Status parsing depends on the exact text format produced by the external
  `mutagen` CLI. This coupling MUST be isolated behind a single
  well-tested parsing boundary so that a change in the CLI's output format
  (or a switch to mutagen's structured/JSON output, if available in the
  target version) requires touching only one component.
- Numeric status codes (FR-3) are an internal implementation detail, not a
  public contract; the rewrite is free to replace them with a proper enum
  as long as the same precedence rules (FR-3/FR-4) are preserved.

## NFR-11 — Testability

- The status-classification logic (FR-3, FR-4), staleness detection
  (FR-6), and auto-resolve matching (FR-10) are pure functions of their
  inputs and MUST be unit-testable without spawning the real `mutagen` CLI
  or a real tray icon.
