# CLAUDE.md

Guidance for Claude Code (and any other agent) working in this repository.

## What this repository is

**MutagenMon** is a Wpf desktop utility that supervises [mutagen.io](https://github.com/mutagen-io/mutagen) file-synchronization sessions and reports their real-time status through a system tray icon. 
It can restart hung/errored sessions automatically and helps resolve sync conflicts 
(manually via a diff/merge tool, or automatically via configured path rules).

The repository currently contains two things:

```
mutagenMon/
├── python/          # EXISTING implementation — wxPython desktop app (source of truth for behavior)
└── requirements/     # Requirements & wireframes extracted from python/, target: a rewrite
```

- `python/` is the **legacy, working implementation**. It is the
  behavioral reference: when in doubt about what the new app should do,
  read this code before asking the user.
- `requirements/` is documentation **derived from** `python/`, written to
  drive a rewrite of this application as a **.NET WPF** desktop app. It is
  not itself the target codebase — see [Where to look first](#where-to-look-first)
  below for the actual .NET project location.

## Where to look first

Always start with [requirements/README.md](requirements/README.md) — it
indexes every requirements document and wireframe, in the recommended
reading order. In particular:

- **[requirements/03-tray-icon-requirements.md](requirements/03-tray-icon-requirements.md)**
  is the most important document in this repository. The tray icon showing
  real-time synchronization status is the single most important
  requirement of the whole application — treat any change touching status
  computation, polling, or the tray icon with the care that priority
  implies.
- **[requirements/05-wpf-migration-notes.md](requirements/05-wpf-migration-notes.md)**
  records the architectural decisions already made for the rewrite (plain
  WPF host + `H.NotifyIcon.Wpf`-based tray icon, background hosted service
  for polling, component mapping). Note its "Revision note": the rewrite
  originally used Blazor Hybrid (WPF + `BlazorWebView`) and was pivoted to
  plain WPF after real Blazor/WebView2 runtime failures on .NET 10 — do
  not reintroduce Blazor/BlazorWebView without a clear reason, and read
  that document's §8 before proposing a different tray-icon approach.

## Working in `python/` (legacy app)

- Entry point: `python/mutagenmon.pyw`. Run with `python mutagenmon.pyw`
  from inside `python/` (requires `wxpython`: `pip install wxpython`).
- Core logic lives in `mutagenmonlib/`:
  - `wx/icon.py` — the tray icon state machine (`TaskBarIcon`). This is
    the file that implements everything described in
    `requirements/03-tray-icon-requirements.md`.
  - `remote/monitor.py` — the background polling thread.
  - `remote/mutagen.py` — `mutagen` CLI wrapper and status text parsing.
  - `remote/resolve.py` — conflict resolution dialogs and logic.
  - `local/file.py`, `local/lib.py`, `local/run.py` — config, formatting,
    and subprocess helpers.
- Configuration: `python/config/config_mutagenmon.json` (JSON with `#`
  comment lines, stripped before parsing). Sessions to monitor are defined
  in `python/mutagen/mutagen-create.bat`.
- Treat this code as **read-mostly**: bug fixes are fine, but do not
  refactor it toward the WPF design — that work belongs in a new project,
  guided by `requirements/`, not in-place inside `python/`.
- Known, intentionally-documented defects (missing icon assets, an
  unreachable "ready + updated" icon state, a generic stale tooltip) are
  listed in `requirements/03-tray-icon-requirements.md` §7. Do not "fix"
  them silently in the legacy app without checking whether the user wants
  legacy parity or a direct fix in the future WPF app instead.

## Working in `requirements/`

- These are **English-only** analysis documents (functional requirements,
  non-functional requirements, screen inventory, migration notes) plus SVG
  wireframes under `requirements/wireframes/`. Every requirement is
  traceable to a specific source file/behavior in `python/` — keep that
  traceability when editing.
- The wireframes are sketches, not pixel-accurate mockups. If the legacy
  UI text/behavior changes, update the corresponding wireframe and
  requirement doc together.
- If you find a new discrepancy between a requirements doc and the actual
  `python/` source, prefer re-reading the source and correcting the doc —
  the running legacy app is the ground truth for *current* behavior.

## The .NET WPF rewrite

The .NET project lives in `dotNet/` (see `dotNet/README.md`). Phase 1
(background polling, session status classification, the tray icon's full
state machine, a minimal context menu) is implemented and build/test-green
on Linux; see the task list / phase notes in
`requirements/05-wpf-migration-notes.md` §6 for what's done and what's
next.

When picking up further phases:

1. Re-read `requirements/05-wpf-migration-notes.md` for the agreed
   architecture (plain WPF + `H.NotifyIcon.Wpf`-style tray icon + a
   background hosted service for polling) before adding anything — note
   its §8 "Runtime pitfalls found during Phase 1 Windows verification",
   which records several non-obvious WPF/tray-icon gotchas already hit and
   fixed once.
2. Follow the phased delivery plan in that document (§6): tray icon +
   polling + status classification first (done), dialogs/menu second,
   conflict resolution third, notifications/auto-resolve fourth,
   auto-recovery/logging last.
3. Keep the new project inside `dotNet/` — do not intermix it with
   `python/`.
4. Preserve configuration key names/defaults from
   `python/config/config_mutagenmon.json` unless the user explicitly asks
   to redesign the config schema (see
   `requirements/05-wpf-migration-notes.md` §5).
5. **Never read from `python/img/` for tray icon bitmaps.** Use
   `requirements/icons/` instead — it already contains every icon
   referenced by `requirements/03-tray-icon-requirements.md` §3 (both
   `.png`, for documentation, and `.ico`, the format actually loaded at
   runtime), including generated placeholders for the three that were
   missing even in the legacy app. `requirements/` is intentionally
   self-contained precisely so that generating the .NET code never needs a
   detour into `python/`. Replace the 3 placeholders (`green-scan`,
   `green-timeout-white`, `green-timeout-red`) with properly designed
   assets before release — see
   `requirements/03-tray-icon-requirements.md` §3.1/§7.1.

## General conventions

- All documentation in this repository is written in **English**, per the
  project owner's instruction, even though the owner communicates with
  Claude in French. Keep new/edited docs in English unless told otherwise.
- This is not (yet) a git repository — there is no branch/commit history to
  consult; rely on the requirements documents and the `python/` source
  itself for context.
