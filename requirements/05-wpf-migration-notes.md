# WPF Migration Notes

These notes translate the legacy wxPython architecture into concepts for a
**full WPF** rewrite. They are guidance for solution design, not a finished
specification — the functional/non-functional requirements documents remain
the source of truth for *behavior*; this document is about *how* to host
that behavior on the WPF stack.

> **Revision note**: this document originally specified a Blazor Hybrid
> host (WPF + `BlazorWebView` for dialogs/status pages). That was dropped
> after Phase 1 hit two real Blazor/WebView2-specific runtime failures on
> Windows (a `WebView2CompositionControl` `FileNotFoundException` on
> `Microsoft.Windows.SDK.NET`, tracked upstream as a .NET 10 regression:
> https://github.com/MicrosoftEdge/WebView2Feedback/issues/5436) on top of
> an extra runtime dependency (WebView2 Runtime) and memory/startup
> overhead that work against NFR-3. The app's actual UI surface — a status
> list and a few simple dialogs — has no need for HTML/CSS, and
> cross-platform (the other usual reason to reach for Blazor Hybrid via
> MAUI) was already explicitly out of scope (see §1). Plain WPF removes
> that whole class of failure instead of working around it.

## 1. Host choice: this decides everything else

Given:

- the project's primary target today is Windows (NFR-4),
- the single most important requirement is a real-time **system tray
  icon** (03-tray-icon-requirements.md), and
- cross-platform desktop is explicitly *not* a launch goal (NFR-4) — the
  legacy app itself was "tested only on Windows" despite the same
  ambition, and nothing here changes that,

the host is a plain **WPF application** plus a dedicated tray icon
library:

- WPF has first-class, mature tray icon support via community libraries
  (e.g. `H.NotifyIcon.Wpf`), directly usable without extra abstraction
  layers.
- No Blazor/WebView2 runtime dependency: one less thing that has to be
  installed on the target machine, one less native/managed interop
  surface that can regress out from under the app on a .NET or WebView2
  update.
- Every dialog/status screen in this app (04-ui-screens-inventory.md) is a
  simple list/text/buttons layout — squarely in WPF's native strength,
  with no rendering or styling need that would justify an embedded browser
  engine.

If cross-platform desktop (macOS/Linux tray) becomes a real near-term
goal, revisit this decision — at that point a cross-platform UI stack with
native tray support (e.g. Avalonia) would be worth evaluating. **Do not
build for cross-platform speculatively.**

## 2. Component mapping

| Legacy (wxPython) | WPF equivalent |
|---|---|
| `wx.adv.TaskBarIcon` (`icon.py`) | `NotifyIcon` (via `H.NotifyIcon.Wpf`) hosted by the WPF app; icon bitmap swap + tooltip text updated from a `DispatcherTimer` (1s tick) |
| `wx.Timer` UI tick (`TaskBarIcon.update`) | `DispatcherTimer` on the WPF UI thread |
| `Monitor` background `threading.Thread` (`monitor.py`) | A hosted background service (`IHostedService` / `BackgroundService` in a generic host) polling the sync engine on its own cadence, exposing state via an injected singleton service (lock-free volatile-reference snapshot), mirroring the legacy get/set-with-lock pattern |
| `queue.Queue` of notification messages | `System.Threading.Channels.Channel<T>` (bounded, single-writer background service, single-reader UI timer) |
| `wx.MessageDialog` / `wx.SingleChoiceDialog` (`resolve.py`, `wx.py`) | Plain WPF `Window`s/`UserControl`s opened on demand from the tray icon (no persistent main window, matching NFR-7) |
| `wx.adv.NotificationMessage` / `ShowBalloon` | Windows toast notifications (`Microsoft.Toolkit.Uwp.Notifications` / `Windows App SDK` notifications), or `NotifyIcon.ShowBalloonTip` as a lighter-weight fallback matching legacy behavior exactly |
| JSON config with `#` comments (`file.py: load_config`) | Hand-rolled whole-line `#`-comment stripping + `System.Text.Json` (see `ConfigLoader` in `MutagenMon.Core`) — keep the file human-editable, no build step |
| `mutagen/mutagen-create.bat` session parsing | Keep parsing the same `.bat`/CLI-args source for drop-in compatibility during transition, but plan a structured session config (e.g. a `sessions.json` array) as the long-term source of truth |
| `subprocess` calls to `mutagen`, `ssh`, `scp` | `System.Diagnostics.Process` wrapped in a single `IMutagenCliClient` abstraction (mirrors NFR-10's "one parsing boundary" requirement) |
| External WinMerge invocation | Same approach: `Process.Start` with configurable executable path; no change in behavior expected |

## 3. Process/threading model

Keep the legacy two-loop model, translated 1:1:

```
WPF App (UI thread)                          Background hosted service
 ├─ NotifyIcon (tray)                         ├─ polls mutagen CLI every
 ├─ DispatcherTimer (1s) ───reads state───────┤  MUTAGEN_POLL_PERIOD
 │    - recompute worst code                  ├─ maintains per-session
 │    - swap icon bitmap + tooltip            │  status/code/conflicts
 │    - drain notification channel            ├─ runs auto-resolve rules
 │    - detect staleness → self-restart       ├─ decides session restarts
 └─ opens a WPF Window on click ───────────────┴─ publishes via a
      (status window / conflict resolution)      thread-safe state holder
```

This preserves the key reliability property from NFR-1/NFR-2: the tray
icon's UI timer must never be blocked by a slow or hung `mutagen` CLI call,
because it only ever reads already-published state.

## 4. WPF windows/controls to build

Directly derived from [04-ui-screens-inventory.md](04-ui-screens-inventory.md):

1. `StatusWindow.xaml` — session list + optional conflicts section
   (replaces screens 3 and 4 with one window parameterized by
   "has conflicts").
2. `GenericMessageDialog.xaml` — reusable title/body/OK[/Cancel] window
   (replaces screens 3/4/7/8/9's shared pattern, per the note in the
   screens inventory).
3. `ConflictResolutionDialog.xaml` — the A/B comparison + radio choice +
   Visual merge/A wins/B wins actions (screen 5).
4. A lightweight "connecting..." indicator (screen 6) — a small
   undecorated `Window`, closed programmatically by the caller.
5. Error display (screen 10) can reuse `GenericMessageDialog.xaml`.

## 5. Configuration & session model

Preserve the *shape* of `config_mutagenmon.json` (same keys, same
defaults) so operators migrating from the legacy app don't have to relearn
tuning knobs (`STATUS_MAX_LAG`, `MUTAGEN_POLL_PERIOD`,
`SESSION_MAX_ERRORS`, etc.) — see
[06-configuration-reference.md](06-configuration-reference.md) for the
full key list, types, defaults, and units (self-contained; no need to open
the legacy config file). Strongly recommended additions for the rewrite:

- Validate config on load (fail fast with a clear message) rather than
  the legacy's implicit trust.
- Make the icon asset set data-driven (one manifest mapping state → asset
  path) so the gaps found in
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §7.1 cannot
  recur silently — a missing asset should fail validation at startup, not
  at first occurrence of that state in production.

## 6. Suggested phased delivery

1. **Phase 1 (parity-critical)**: background polling service, session
   status classification (FR-2/FR-3/FR-4), tray icon with the full state
   matrix and click behavior (FR-5, all of 03-tray-icon-requirements.md),
   staleness/self-restart (FR-6). This alone delivers the "most important
   requirement" stated by the business. **Done** — see `dotNet/README.md`.
2. **Phase 2**: status view + context menu actions (FR-7, FR-8), now as a
   native `StatusWindow` per §4 above instead of a Blazor page. **Done** —
   see `dotNet/README.md`. The status view's "Resolve conflicts" action
   (FR-8.2) is wired to a placeholder; the actual workflow is Phase 3.
3. **Phase 3**: manual conflict resolution workflow (FR-9) and visual
   merge integration. **Done** — see `dotNet/README.md`.
4. **Phase 4**: automatic conflict resolution (FR-10), notifications
   (FR-11), profile-change detection (FR-12).
   - [x] FR-10 — automatic conflict resolution (ordered regex rules,
     `AUTORESOLVE_HISTORY_AGE` grace period). **Done** — see
     `dotNet/README.md`.
   - [x] FR-11.1/FR-11.2/FR-11.4 — desktop notifications for new conflicts,
     auto-resolve, and confirmed profile update. **Done** — see
     `dotNet/README.md`. FR-11.3 (stuck-connection-restart notification)
     moved to Phase 5 below — it depends on FR-13's per-session restart
     trigger, which doesn't exist yet.
   - [x] FR-12 — session profile change detection (archive mtime watch,
     debounced via `MUTAGEN_PROFILE_GRACE`). **Done** — see
     `dotNet/README.md`.
5. **Phase 5**: session auto-recovery (FR-13) and logging/diagnostics
   polish (FR-14).
   - [ ] FR-13 — automatic session recovery (restart on
     `SESSION_MAX_NOSESSION`), plus the FR-11.3 stuck-connection-restart
     notification it gates.
   - [ ] FR-14 — logging/diagnostics polish (base logging already **Done**
     in Phase 1, see §7 below; this item covers the remaining FR-14 items
     not yet mapped, e.g. FR-14.3 resolution-log cross-referencing).

Each `[ ]`/`[x]` line is a standalone tracking unit: check the box, and
append a one-line status note (**Done** — see `dotNet/README.md`, or
**Skipped** — with a reason) the moment that specific FR is finished or
consciously deferred — don't wait for every FR in the phase to land before
updating this list. This lets a phase be worked on FR-by-FR (e.g. "do only
FR-10 of Phase 4") without losing track of what's left.

Each phase should close with the corresponding gaps from
03-tray-icon-requirements.md §7 explicitly triaged (fixed or consciously
deferred), not silently carried over.

## 7. Logging

Implemented in Phase 1 (`MutagenMon.App/App.xaml.cs`,
`MutagenMon.App/FileLoggerProvider.cs`), and deliberately simpler than the
legacy design described in
[01-functional-requirements.md FR-14](01-functional-requirements.md#fr-14--logging--diagnostics)
— see that section for the FR-14.1/14.2/14.3 mapping and rationale. The
concrete decisions:

- **No third-party logging library**: logging is a small hand-rolled
  `Microsoft.Extensions.Logging.ILoggerProvider`/`ILogger`
  (`FileLoggerProvider`) writing plain-text lines to a file — nothing
  beyond `Microsoft.Extensions.Logging.Abstractions`, which the app
  already depends on for the standard `ILogger<T>` DI pattern used
  throughout `MutagenMon.Core`. A third-party library (Serilog) was used
  initially and removed — it added a dependency for something this small
  a hand-rolled provider covers directly, with full control over failure
  behavior (see the last bullet below).
- **Every call is self-contained**: each log call opens, appends, and
  closes the target file — no persistent file handle is held between
  calls. Nothing needs to be flushed or disposed on reconfigure or on
  shutdown, and the file is never held open across a blocking `MessageBox`
  dialog.
- **Single primary sink, always on**: one file
  (`log/mutagenMon.log`, or `<LOG_PATH>/mutagenMon.log` if `LOG_PATH` is
  configured) captures every level from Debug upward, unconditionally. No
  separate debug file, no verbosity gate. `DEBUG_LEVEL` stays in
  `config_mutagenmon.json` for key-compatibility with the legacy config
  (per §5's "preserve config shape") but currently has no effect.
- **Two-stage configuration**: `FileLoggerProvider` is constructed with a
  default path (`"log"`) *before* `config_mutagenmon.json` is even read,
  then re-pointed via `SetPrimaryLogPath` once the real `LOG_PATH` is
  known. This guarantees a failure while loading config itself still
  produces a log entry, instead of failing before logging exists at all.
- **`LOG_PATH` resolution**: resolved relative to the executable's
  directory, *unless* it is an absolute/rooted path (`Path.IsPathRooted`),
  in which case it is used as-is — lets an operator redirect logs to a
  fixed location (e.g. a synced folder, a central logs drive) regardless
  of where the app is installed.
- **Startup is fully traced**: every step of `OnStartup` (config load,
  session file parsing, host build/start, tray icon acquisition, tray
  controller start) logs at Information, and the entire method body is
  wrapped in one try/catch that logs any exception at Critical and shows
  it in a blocking `MessageBox` — see FR-14.1. Global handlers
  (`Dispatcher.UnhandledExceptionFilter`, `DispatcherUnhandledException`,
  `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`)
  catch anything thrown later, anywhere else in the app, the same way —
  including exceptions raised inside a nested dispatcher frame (opening a
  `Popup`/`ContextMenu`), which `DispatcherUnhandledException` alone can
  miss.
- **Why simpler than legacy**: during Phase 1 manual verification, a
  startup exception (a malformed `config_mutagenmon.json`) produced *zero*
  log output and a silently-dead process, because the legacy-equivalent
  design (configure logging only after config loads successfully, gate
  verbose output behind a flag that defaults to off) meant nothing was
  listening yet at the exact moment it mattered most. Always-on,
  single-file logging removes that failure mode entirely and is simple
  enough not to need its own configuration story.
- **A caught exception can still fail to reach disk — mitigated by
  design, not by a diagnostic bolt-on**: `LOG_PATH` can point inside a
  folder mutagen itself syncs (as it does during development — this
  repository's own working copy is synced between the Linux dev box and
  the Windows test machine), so a write landing exactly when the sync
  engine (or a remote reader) touches the same file is a realistic way for
  a log write to fail (observed during Phase 1 with a third-party logging
  library: an `ObjectDisposedException` from `TaskbarIcon.Icon` reuse
  triggered the error dialog but silently left no log entry at all,
  because that library swallows sink-level write failures by default).
  `FileLoggerProvider` addresses this directly rather than papering over
  it with a self-diagnostic log: every write is wrapped in its own
  try/catch, a failure is reported to `System.Diagnostics.Debug` (visible
  in Visual Studio's Output window) immediately, and — critically — a
  second, fixed-location file (`mutagenMon.fatal.log`, next to the
  executable, deliberately *not* under `LOG_PATH`) always receives every
  Critical-level entry, so a crash is never lost to a transient failure on
  the primary sink.

## 8. Runtime pitfalls found during Phase 1 Windows verification

Kept here so they aren't rediscovered from scratch on a future machine/
.NET upgrade:

- **`TaskbarIcon.IconSource` is not conversion-free**: despite exposing an
  `ImageSource` property, `H.NotifyIcon.Wpf`'s `IconSource` setter
  internally converts to a `System.Drawing.Icon`
  (`StreamExtensions.ToSmallIcon`), which throws for some PNGs
  (`ArgumentException: Argument 'picture' must be a picture that can be
  used as a Icon.`). Fix: generate real `.ico` files (square-padded,
  multi-resolution) and load them via `TaskbarIcon.Icon` directly — see
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §3.1.
- **A windowless `TaskbarIcon` needs `ForceCreate()`**: with no main
  window, the native tray icon is never created implicitly (that normally
  happens on `Loaded`, which never fires for a resource only ever
  referenced from code). Call `trayIcon.ForceCreate()` right after
  resolving it — this is the pattern H.NotifyIcon's own "windowless"
  sample app uses.
- **`InvariantGlobalization` breaks WPF's default control templates**:
  `<InvariantGlobalization>true</InvariantGlobalization>` (a reasonable
  default for `MutagenMon.Core`/`.Tests`, which have no WPF dependency)
  must be overridden to `false` in the WPF app project. WPF's default
  `ContextMenu` template resolves a specific culture via
  `XmlLanguage.GetSpecificCulture()`, which throws
  (`XamlParseException: Cannot find non-neutral culture related to
  'en-us'`) under invariant globalization mode.
- **Exceptions inside H.NotifyIcon's native WndProc callback crash
  silently**: `TaskbarIcon.ShowContextMenu()` has no try/catch and runs
  synchronously inside the library's own native window-message callback (a
  reverse P/Invoke boundary). .NET always fail-fasts the whole process on
  an exception escaping such a callback, before any managed exception
  handler runs — so a bug there (e.g. the `InvariantGlobalization` one
  above, before it was fixed) was completely unlogged. Fix implemented in
  `TrayIconController`: intercept `TaskbarIcon.PreviewTrayContextMenuOpen`,
  cancel the synchronous attempt (`e.Handled = true`), and reissue it via
  `Dispatcher.BeginInvoke` — a normal managed call stack that the regular
  exception handlers can catch.
