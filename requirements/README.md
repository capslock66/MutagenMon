# MutagenMon — Requirements

This folder documents the required behavior of MutagenMon. It was
originally reverse-engineered from a legacy application (since removed
from this repository) and now serves as the specification against which
the **.NET WPF** application is built and verified.

## Reading order

1. [01-functional-requirements.md](01-functional-requirements.md) — all
   functional requirements (FR-1 .. FR-15), each traceable to source code.
2. [02-non-functional-requirements.md](02-non-functional-requirements.md)
   — quality attributes (NFR-1 .. NFR-11).
3. **[03-tray-icon-requirements.md](03-tray-icon-requirements.md)** — ⭐
   the single most important requirement: the tray icon must display the
   real-time status of all synchronization sessions. Full icon-state
   matrix, timing, click behavior, and self-healing rules.
4. **[06-configuration-reference.md](06-configuration-reference.md)** —
   every `config_mutagenmon.json` key, its type, default value, and unit.
   Referenced throughout the documents above so no default value ever
   needs to be guessed.
5. [UserTests.md](UserTests.md) — manual, click-by-click acceptance test
   script for verifying each requirement against the running application,
   kept in step with implementation progress.

## Icon assets

[icons/](icons) contains every tray icon bitmap referenced by
[03-tray-icon-requirements.md](03-tray-icon-requirements.md) §3 — 12 real
assets copied from the legacy app plus 3 generated placeholders for icons
the legacy app references but never actually shipped (see §3.1 and §7.1 of
that document for details). **This makes `requirements/` self-contained:
implementing the .NET WPF tray icon never requires the legacy app's icon
folder, which has since been removed from this repository.**

## Scope note

The application is now the executable reference for behavior.
Where these documents and the running application disagree, treat it as a
documentation bug to fix here — except for the deliberate gaps listed in
03-tray-icon-requirements.md §7, which are known legacy defects the
rewrite intentionally fixed rather than copied.
