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
