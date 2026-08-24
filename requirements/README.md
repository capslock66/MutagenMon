# MutagenMon — Requirements

This folder documents the behavior of the existing wxPython application
(`python/`), reverse-engineered from its source code, as the specification
baseline for a rewrite as a **.NET WPF** desktop application.

## Reading order

1. [00-application-overview.md](00-application-overview.md) — what the
   app does, its architecture, module map, and known implementation
   quirks.
2. [01-functional-requirements.md](01-functional-requirements.md) — all
   functional requirements (FR-1 .. FR-15), each traceable to source code.
3. [02-non-functional-requirements.md](02-non-functional-requirements.md)
   — quality attributes (NFR-1 .. NFR-11).
4. **[03-tray-icon-requirements.md](03-tray-icon-requirements.md)** — ⭐
   the single most important requirement: the tray icon must display the
   real-time status of all synchronization sessions. Full icon-state
   matrix, timing, click behavior, and self-healing rules.
5. [04-ui-screens-inventory.md](04-ui-screens-inventory.md) — every screen/
   dialog in the legacy app, with links to its wireframe.
6. [05-wpf-migration-notes.md](05-wpf-migration-notes.md)
   — how to host this behavior on the WPF stack (component mapping,
   threading model, phased delivery plan).
7. **[06-configuration-reference.md](06-configuration-reference.md)** —
   every `config_mutagenmon.json` key, its type, default value, and unit.
   Referenced throughout the documents above so no default value ever
   needs to be looked up in `python/config/config_mutagenmon.json`.
8. [UserTests.md](UserTests.md) — manual, click-by-click acceptance test
   script for verifying each requirement against the running `dotNet/`
   application, kept in step with implementation progress.

## Icon assets

[icons/](icons) contains every tray icon bitmap referenced by
[03-tray-icon-requirements.md](03-tray-icon-requirements.md) §3 — 12 real
assets copied from the legacy app plus 3 generated placeholders for icons
the legacy app references but never actually shipped (see §3.1 and §7.1 of
that document for details). **This makes `requirements/` self-contained:
implementing the .NET WPF tray icon never requires reading anything from
`python/img/`.**

## Wireframes

Sketches (not pixel-accurate screenshots) of every screen the legacy
application can show, in [wireframes/](wireframes):

- [tray-icon-states.svg](wireframes/tray-icon-states.svg) — the full icon
  state matrix (the core deliverable of this analysis).
- [tray-context-menu.svg](wireframes/tray-context-menu.svg)
- [status-dialog-ok.svg](wireframes/status-dialog-ok.svg)
- [status-dialog-conflicts.svg](wireframes/status-dialog-conflicts.svg)
- [conflict-resolution-dialog.svg](wireframes/conflict-resolution-dialog.svg)
- [remote-connecting-toast.svg](wireframes/remote-connecting-toast.svg)
- [error-dialog.svg](wireframes/error-dialog.svg)
- [notification-toast.svg](wireframes/notification-toast.svg)

## Scope note

The legacy `python/` application remains the executable reference for
behavior. Where these documents and the running legacy app disagree, treat
it as a documentation bug to fix here — except for the deliberate gaps
listed in 03-tray-icon-requirements.md §7, which are known legacy defects
the rewrite should fix rather than copy.
