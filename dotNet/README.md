# MutagenMon — .NET WPF rewrite

This is the .NET rewrite scaffolded per
[requirements/05-wpf-migration-notes.md](../requirements/05-wpf-migration-notes.md).
Current scope: **Phase 0 (scaffolding) + Phase 1 (real-time tray icon
core) + Phase 2 (context menu & status view)** — background polling,
session status classification/aggregation, the tray icon's full state
machine, the full context menu (reload/stop-start/show status/exit, FR-7),
and the status view with a conflicts section (FR-8). Manual conflict
resolution, notifications, automatic conflict resolution, and session
auto-restart execution are **not yet implemented** — see "Out of scope" in
the plan this was built from.

> This started as a Blazor Hybrid app (WPF + `BlazorWebView`) and was
> pivoted to plain WPF after real Blazor/WebView2 runtime failures on
> .NET 10 — see the migration notes' revision note for why.

## Solution layout

```
dotNet/
├── MutagenMon.sln
├── Directory.Build.props
└── src/
    ├── MutagenMon.Core/            # net10.0 — no WPF deps, fully testable on any OS
    ├── MutagenMon.Core.Tests/      # xunit v3 — runs on Linux/macOS/Windows
    └── MutagenMon.App/             # net10.0-windows, WPF host — Windows-only at runtime
```

## Building (works on Linux, macOS, or Windows)

`MutagenMon.App` targets `net10.0-windows` with
`EnableWindowsTargeting=true`, which pulls the Windows Desktop reference
assemblies from NuGet — so the **whole solution builds on any OS**,
without needing an actual Windows machine:

```bash
dotnet build MutagenMon.sln
```

```bash
dotnet test src/MutagenMon.Core.Tests/MutagenMon.Core.Tests.csproj
```

`MutagenMon.Core`'s classification/staleness/tray-icon-state logic has zero
dependency on a real `mutagen` binary or a real tray icon (NFR-11) —
`SessionMonitorServiceTests` drives the whole pipeline through a fake CLI
client, and `TrayIconStateResolverTests` is a parametrized test over every
row of
[requirements/03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md)
§3's decision table. Both run and pass on Linux.

## Running / verifying on Windows (required — WPF/tray icon are Windows-only)

`MutagenMon.App` cannot run or be visually verified on Linux — WPF and the
tray icon need an actual Windows desktop session. On a Windows machine:

1. Install a [mutagen.io](https://github.com/mutagen-io/mutagen) release
   and place the binary at `src/MutagenMon.App/mutagen/mutagen.exe`
   (or edit `MUTAGEN_PATH` in `src/MutagenMon.App/config/config_mutagenmon.json`).
2. Edit `src/MutagenMon.App/mutagen/mutagen-create.bat` to point at two
   real local folders (the shipped sample uses
   `C:\MutagenMonTest\alpha`/`beta` — create them, or change the paths).
3. Run:
   ```bash
   dotnet run --project src/MutagenMon.App
   ```
   Or in Visual Studio: open `MutagenMon.sln`, set **MutagenMon.App** as the
   Startup Project (Solution Explorer → right-click it → *Set as Startup
   Project*), then F5/Ctrl+F5. The project's `AssemblyName` is `MutagenMon`,
   so the built executable is `MutagenMon.exe`, but the *project* to launch
   is `MutagenMon.App`.
4. **Checklist:**
   - A tray icon appears immediately in the "waiting for status" state
     (light gray) — matches `SessionStatusCode.Unknown` in
     [03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md) §3.
   - As mutagen scans/syncs/settles, the icon and its tooltip move through
     Scanning → Syncing → Ready, matching that same table exactly.
   - Left-click, or right-click → **Show status**, opens the status view:
     one Name/Status/Alpha/Beta block per configured session, and — if any
     session has an unresolved conflict — a CONFLICTS section plus a
     "Resolve conflicts" button (currently a placeholder message; the real
     workflow is FR-9, Phase 3).
   - Right-click → **Reload config & restart mutagen** terminates every
     running session, then restarts the whole process once they've all
     stopped (watch the context menu collapse to a disabled
     "Restarting..." item in the meantime, and check `log/mutagenMon.log`
     for the restart entry).
   - Right-click → **Stop Mutagen sessions** terminates every running
     session and flips the item to **Start Mutagen sessions**; the tray
     icon should reflect the "disabled" variant of its current state
     (see `Enabled` in the icon decision table). Note: re-enabling does
     not itself relaunch a terminated session — that's the auto-recovery
     logic, FR-13, Phase 5 — so sessions stay down until the next
     "Reload config & restart mutagen" or a manual app restart.
   - Right-click → **Exit MutagenMon** removes the tray icon and closes
     the process cleanly.
   - Tray icon assets are loaded from `Assets/Icons/*.ico` via
     `TaskbarIcon.Icon` (a `System.Drawing.Icon`), **not**
     `TaskbarIcon.IconSource` — see
     [03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md)
     §3.1 for why (`IconSource`'s internal PNG→Icon conversion is fragile
     and threw `ArgumentException: Argument 'picture' must be a picture
     that can be used as a Icon.` during manual verification).
   - **Staleness check:** temporarily rename/move `mutagen.exe` so polling
     starts failing, and watch the icon degrade
     Info (pale) → Warning → Error (red-tinted "stale" icons) on the
     schedule in `STATUS_MAX_LAG`. Past the `Restart` threshold (90s by
     default) the app should self-restart (spawn a new process, tray icon
     reappears) — check `log/mutagenMon.log` for the restart entry.

## Logging

Logging is a small hand-rolled `ILoggerProvider` (`FileLoggerProvider.cs`)
— no third-party logging library — configured in `App.xaml.cs`. Every log
call opens, appends, and closes its target file (no persistent handle, so
nothing to flush/dispose). The primary log is written under the directory
named by `LOG_PATH` in `config_mutagenmon.json` (default `"log"`, resolved
relative to `MutagenMon.exe`'s folder) — or, if `LOG_PATH` is an absolute
path (e.g. `"c:\\logs\\mutagenmon"`), that exact directory is used as-is
instead:

- **`log/mutagenMon.log`** — a single file capturing **every level**
  (Debug and above), always — no separate debug file, no `DEBUG_LEVEL`
  gating (`DEBUG_LEVEL` in `config_mutagenmon.json` is currently unused).
  This is also where startup failures land: every step of `OnStartup`
  (config load, session parsing, host build/start, tray icon creation) is
  wrapped in a single try/catch that logs any exception here at Critical
  level *and* pops a `MessageBox` with the full exception — added
  specifically so a failed launch (e.g. a missing `config_mutagenmon.json`
  or `mutagen-create.bat`) is never silent. Unhandled exceptions anywhere
  else in the app (UI thread, background threads, unobserved task
  exceptions) are also caught globally and logged here.
- **`mutagenMon.fatal.log`** (next to `MutagenMon.exe`, i.e. *not* under
  `LOG_PATH`) — a redundant copy of Critical-level entries only. Exists
  because `LOG_PATH` can point inside a folder mutagen itself syncs (a
  reasonable thing to do, but it means the log file can occasionally be
  briefly locked/touched by the sync engine or a remote reader exactly when
  a write happens); this fallback sink is always at a fixed location, so a
  crash is never lost to a transient write failure on the primary sink. Any
  write failure on the primary sink (whatever the level) is also reported
  to the Visual Studio Output window (`System.Diagnostics.Debug`) and, on a
  best-effort basis, to this same fallback file.

If nothing appears in the tray and the app seems to do nothing: check
`log/mutagenMon.log` first — a startup exception there (most commonly a
missing/misconfigured `config/config_mutagenmon.json`,
`mutagen/mutagen-create.bat`, or an invalid `MUTAGEN_PATH`) is now
guaranteed to be logged and shown in a message box instead of silently
killing the process. If even that's empty, check `mutagenMon.fatal.log`.

## Known limitations of this phase

- The status view's "Resolve conflicts" button (FR-8.2) is wired but shows
  a placeholder message — the actual manual resolution workflow (FR-9) is
  Phase 3.
- No desktop notifications (FR-11), no automatic session restart execution
  (FR-13) — "Start Mutagen sessions" re-enables monitoring but does not
  itself relaunch a session that "Stop Mutagen sessions" (or a poll
  failure) terminated; that revival is the auto-recovery logic, Phase 5.
- Three icon assets (`green-scan`, `green-timeout-white`,
  `green-timeout-red`) are generated placeholders, not final design assets
  — see [requirements/03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md) §3.1/§7.1.
