# Tray Icon Requirements — ⭐ Highest-priority requirement

> The system tray icon is the application's **only permanent UI surface**
> and its **primary reason for existing**: it must show, at a glance and in
> real time, the worst-case health of every monitored synchronization
> session. Every other feature (dialogs, notifications, menus) is
> secondary and reachable *from* the tray icon. In the WPF rewrite,
> reproducing this component faithfully — visuals, timing, click
> behavior, and self-healing — is the top priority.

See wireframe: [wireframes/tray-icon-states.svg](wireframes/tray-icon-states.svg),
[wireframes/tray-context-menu.svg](wireframes/tray-context-menu.svg).

Actual icon bitmaps are provided in [icons/](icons) (self-contained in this
`requirements/` folder — see §3.1 below), in two formats: `.png` (source
image, kept for documentation/preview purposes since it renders inline in
Markdown viewers) and `.ico` (the format the .NET implementation MUST
actually load at runtime — see §3.1 for why). The .NET implementation MUST
NOT need to read anything from `python/img/`; every asset referenced by
this document is already copied (or, for the three that never existed,
placed as a labelled placeholder) under `requirements/icons/`.

## 1. Update loop

- TIC-1: A UI-side timer MUST tick every **1000 ms** (configurable in
  spirit, hard-coded today) and, on each tick:
  1. re-evaluate whether the on-disk sync profile/archive changed
     (see FR-12),
  2. recompute the aggregated ("worst") status code across all sessions
     (FR-4),
  3. set the icon bitmap and tooltip text accordingly (§3),
  4. drain and display any queued notification (FR-11),
  5. check for newly-appeared conflicts and notify if needed (FR-11.1).
- TIC-2: This UI timer reads state published by an independent background
  poller (FR-2) — it MUST NOT itself call into the sync engine
  synchronously, to guarantee the icon never freezes even if the sync
  engine is slow or hung.
- TIC-3: The very first icon shown at startup, before any poll has
  completed, MUST be a distinct "waiting for status" state (§3, code `0`).

## 2. Aggregation rule

- TIC-4: The icon always represents the **single worst session** among all
  configured sessions (minimum numeric code, FR-4). It never shows a
  per-session breakdown directly — per-session detail is one click away
  (FR-8).

## 3. Full icon-state decision table

State is a function of four inputs: aggregated code (FR-3/FR-4), whether
monitoring is currently **enabled** (user has not paused it), whether the
sync profile was just **updated** on disk (FR-12), and the **staleness
tier** of the last successful poll (computed from `now − last_poll_time`
against the `STATUS_MAX_LAG` thresholds, `Info < Warning < Error`).

Staleness tiers apply uniformly on top of the "ready / conflicts / problems
/ syncing / scanning" rows below — i.e. any of those five states can be
shown in its normal form, or downgraded to Info-stale / Warning-stale /
Error-stale if the underlying data hasn't refreshed recently. Past the
`Restart` threshold the app self-restarts instead of showing an icon at all
(TIC-9).

| Code | State | Enabled | Updated | Stale tier | Tooltip suffix | Icon asset — preview / runtime ([icons/](icons)) |
|---|---|---|---|---|---|---|
| `0` | Waiting for first status | — | — | — | "waiting for status..." | [`lightgray-init.png`](icons/lightgray-init.png) / [`.ico`](icons/lightgray-init.ico) |
| `100` | Ready / watching for changes | yes | no | none | "mutagen is watching for changes" | [`green.png`](icons/green.png) / [`.ico`](icons/green.ico) |
| `100` | Ready, freshly updated | yes | **yes** | none | "mutagen is watching for changes (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) *(see quirk §6.2 — currently unreachable)* |
| `100` | Ready, but monitoring being turned off | **no** | — | any | "mutagen is stopping" | [`green-stop.png`](icons/green-stop.png) / [`.ico`](icons/green-stop.ico) |
| `60` | Conflicts detected | yes | — | none | "conflicts" | [`green-conflict.png`](icons/green-conflict.png) / [`.ico`](icons/green-conflict.ico) |
| `50` | Problems detected | yes | — | none | "problems" | [`green-error.png`](icons/green-error.png) / [`.ico`](icons/green-error.ico) |
| `40` | Syncing | yes | no | none | "mutagen is syncing" | [`green-sync.png`](icons/green-sync.png) / [`.ico`](icons/green-sync.ico) |
| `40` | Syncing, freshly updated | yes | **yes** | none | "mutagen is syncing (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) |
| `30` | Scanning | yes | no | none | "mutagen is scanning" | [`green-scan.png`](icons/green-scan.png) / [`.ico`](icons/green-scan.ico) *(⚠ placeholder — asset never existed, §6.1)* |
| `30` | Scanning, freshly updated | yes | **yes** | none | "mutagen is scanning (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) |
| `100/60/50/40/30` | Any of the above, stale (Info tier) | yes | — | **Info** | "mutagen is watching for changes (stale)" | [`green-timeout-white.png`](icons/green-timeout-white.png) / [`.ico`](icons/green-timeout-white.ico) *(⚠ placeholder — asset never existed, §6.1)* |
| `100/60/50/40/30` | Any of the above, stale (Warning tier) | yes | — | **Warning** | "mutagen is watching for changes (stale)" | [`green-timeout.png`](icons/green-timeout.png) / [`.ico`](icons/green-timeout.ico) |
| `100/60/50/40/30` | Any of the above, stale (Error tier) | yes | — | **Error** | "mutagen is watching for changes (stale)" | [`green-timeout-red.png`](icons/green-timeout-red.png) / [`.ico`](icons/green-timeout-red.ico) *(⚠ placeholder — asset never existed, §6.1)* |
| `-1` | No session found, recovering | **yes** | — | — | "mutagen is not running (starting)" | [`darkgray-restart.png`](icons/darkgray-restart.png) / [`.ico`](icons/darkgray-restart.ico) |
| `-1` | No session found, paused by user | **no** | — | — | "mutagen is not running (disabled)" | [`darkgray.png`](icons/darkgray.png) / [`.ico`](icons/darkgray.ico) |
| `-2` | Cannot connect, recovering | **yes** | — | — | "error (starting)" | [`orange-restart.png`](icons/orange-restart.png) / [`.ico`](icons/orange-restart.ico) |
| `-2` | Cannot connect, paused by user | **no** | — | — | "error (disabled)" | [`orange.png`](icons/orange.png) / [`.ico`](icons/orange.ico) |

### 3.1 Icon asset inventory

All 15 bitmaps referenced above are provided in two formats in
[`requirements/icons/`](icons), so the .NET implementation has everything
it needs without reading `python/img/`:

- **`.png`** — the source image, kept only so the table above and GitHub
  previews render inline (most Markdown viewers do not render `.ico`).
  **Not** the format loaded by the .NET app.
- **`.ico`** — square-padded (transparent padding to the larger of
  width/height, since the source PNGs are not square — e.g. 512×390), then
  rendered into a multi-resolution icon (16/32/48/256 px) with `magick
  convert -define icon:auto-resize=16,32,48,256`. This is the format the
  .NET implementation **MUST** load at runtime, via
  `TaskbarIcon.Icon` (a `System.Drawing.Icon`), **not**
  `TaskbarIcon.IconSource` (a WPF `ImageSource`). Reason: the Windows tray
  (`Shell_NotifyIcon`) needs a native `HICON` regardless of which property
  is used; `IconSource`'s setter resolves this by converting the
  `ImageSource` to an `Icon` internally (H.NotifyIcon.Wpf's
  `StreamExtensions.ToSmallIcon`), and that GDI+ conversion throws
  `ArgumentException: Argument 'picture' must be a picture that can be
  used as a Icon.` for some PNGs (observed in practice during Phase 1
  manual verification). Loading a real `.ico` directly via the `Icon`
  property sidesteps that conversion entirely.
- **11 real, verified source images** copied unchanged from the legacy
  `python/img/` folder: `green`, `green-success`,
  `green-stop`, `green-conflict`, `green-error`,
  `green-sync`, `green-timeout`, `darkgray`,
  `darkgray-restart`, `orange`, `orange-restart`.
- **1 WPF-specific asset derived from a legacy image**: `lightgray-init`
  (code `0`, "waiting for first status") is the legacy `lightgray.png`
  with a badge added — the same blue circle badge already used for
  `darkgray-restart`/`orange-restart`, but with three dots instead of the
  restart arrow — so the very first icon shown at process startup (before
  config/session loading and DI container build even run, see
  `App.xaml.cs`) visibly reads as "initializing" rather than looking like
  a stalled/broken tray icon. Renamed from the legacy's plain `lightgray`
  because that name no longer described what the icon shows. This is a
  deliberate rewrite-only improvement, not a legacy behavior to preserve —
  `python/` keeps using its own unmodified `lightgray.png`.
- **3 generated placeholders** for assets that are referenced by
  `icon.py` but were **never present** in `python/img/` even in the
  legacy app (a real, pre-existing bug — §6.1): `green-scan`,
  `green-timeout-white`, `green-timeout-red`. These are simple
  colored-circle stand-ins (green with a scan-sweep stripe; pale
  green/white; red) good enough to unblock building the full state
  machine, but they are **not final design assets** — replace them with
  properly designed icons (ideally vector/SVG source scaled per-DPI, per
  §8) before shipping. Regenerate their `.ico` alongside any redesign.
- Unused legacy files in `python/img/` (`blue.png`, `cyan.png`,
  `folder.png`, `gray.png`, `remote-connection.png`, `resolve.png`,
  `status.png`, `yellow.png`, and the numbered animation-frame variants
  `green-stop2..5.png`, `green-sync2.png`, `green-timeout2/3/5.png`) are
  **not referenced anywhere in the source code** and were intentionally
  **not** copied — they are legacy cruft, not part of this requirement.

Every tooltip is prefixed with the configured application name
(`TRAY_TOOLTIP`, default "MutagenMon") followed by `: `, e.g.
`"MutagenMon: mutagen is watching for changes"`.

Note the deliberate priority ordering when several conditions could apply
at once: **staleness overrides "updated"** (a session can't be both "just
refreshed" and "not refreshed in a while"), and **conflicts/problems
override "ready"** even if the profile also updated — a user must never see
a falsely-reassuring green "ready/updated" icon while conflicts are
pending.

## 4. Tooltip requirements

- TIC-5: The tooltip MUST always be a short, single-line, human-readable
  sentence (not raw codes/JSON) — see column above for exact wording per
  state.
- TIC-6: The tooltip MUST update in lock-step with the icon (same tick,
  never showing a stale icon with a fresh tooltip or vice versa).

## 5. Interaction requirements

- TIC-7 (primary click / left-click): MUST open the detailed status view
  (FR-8) — a synchronous, on-demand read of full status text, not cached
  from the last tick.
  - If there are unresolved conflicts, this view MUST offer to launch the
    conflict-resolution workflow (FR-9) as its primary action, with a
    clearly secondary "dismiss" action.
  - If there are no unresolved conflicts, it MUST be a plain
    informational view with a single dismiss action.
- TIC-8 (secondary click / right-click): MUST open a context menu
  (FR-7) with, in order: reload/restart, start-or-stop toggle (labelled
  according to current enabled state), separator, show status, separator,
  exit — collapsing to just "Restarting…" (disabled) + "Exit" while a
  restart is in flight.

## 6. Robustness requirements

- TIC-9: If the OS fails to (re-)install the tray icon after a `set icon`
  call, the application MUST treat this as fatal for the current process
  and trigger a full self-restart (new process spawned, current one
  exits) rather than continuing with a missing/broken icon.
- TIC-10: If the background poller's last successful update exceeds the
  `Restart` staleness threshold (default 90s), the application MUST
  self-restart even if the tray icon itself still looks fine — a
  non-updating icon that still displays an old "ready" state is worse than
  a visible restart.

## 7. Known gaps to close in the rewrite (do not silently replicate)

These are bugs/inconsistencies found in the current implementation. The
WPF rewrite MUST make an explicit decision on each (fix by default, unless
there is a reason to intentionally preserve legacy behavior for parity
during a transition period).

1. **Missing icon assets referenced in code**: `green-scan.png`,
   `green-timeout-white.png`, and `green-timeout-red.png` are referenced
   by the status logic but do not exist in `python/img/`. The scanning and
   two of the three staleness tiers currently have no real bitmap.
   [`requirements/icons/`](icons) ships generated placeholders for these
   three (§3.1) purely so the rewrite is unblocked without touching
   `python/` — they are **not** approved design assets. The rewrite MUST
   ship a complete, properly designed asset for every row of the table in
   §3 (e.g. as SVG/vector icons scaled per-DPI in .NET, rather than
   fixed-size PNGs) before release.
2. **"Ready + updated" is unreachable**: because of how the legacy
   condition is structured (`if worst_code == 100 and not updated_profile`
   with no later `elif` re-testing code `100`), the "just synced, all
   caught up" flash never actually shows when the aggregated state is
   "ready", even though this is the state that intuitively benefits most
   from a "just updated" flash. The rewrite MUST make this state reachable
   for every base state in §3, not only syncing/scanning.
3. **Generic stale tooltip loses context**: all three staleness tiers use
   the same "watching for changes (stale)" wording regardless of whether
   the last known state was conflicts, problems, syncing, or scanning. The
   rewrite SHOULD keep the last known state name in the stale tooltip,
   e.g. `"mutagen has conflicts (stale, no update for 32s)"`.
4. **No de-duplication guard on `SetIcon`**: the legacy code sets the icon
   bitmap on every tick regardless of whether the state actually changed
   (a de-dup check exists in the source but is commented out). The rewrite
   SHOULD only touch the underlying OS tray API when the visual state
   actually changes, to minimize flicker and OS-level overhead.

## 8. WPF implication (see also 05-wpf-migration-notes.md)

- **.NET's WPF has no built-in tray icon API** — the equivalent of
  `wx.adv.TaskBarIcon` must come from a platform integration such as
  `NotifyIcon` (WinForms/WPF interop) or a dedicated tray-icon library
  (`H.NotifyIcon.Wpf`). This is a **make-or-break architectural decision**
  for the rewrite and must be validated early (see the migration notes
  document), because everything in this document depends on that host
  supporting: a per-DPI icon bitmap swap at ≤1s cadence, a tooltip, a
  left-click event distinct from opening the context menu, and a native
  context menu (or an equivalent quick popup) without requiring a full
  application window to be visible.
- The window(s) used for the detailed status view (FR-8) and conflict
  resolution (FR-9) can be shown in a small popup/host window opened
  on-demand from the tray icon, mirroring the legacy "no main window,
  dialogs on demand" model (NFR-7).
