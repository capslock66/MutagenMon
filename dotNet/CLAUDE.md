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
