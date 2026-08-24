# UI Screens Inventory (wxPython source)

The application has **no traditional main window**. Every screen listed
below is either the tray icon itself, a menu popped from it, or a modal/
modeless dialog opened on demand. This inventory is the basis for the
wireframes in [wireframes/](wireframes) and for the window/dialog list of
the WPF rewrite.

Exact button labels, dialog titles, and body text for every screen below
are specified in [01-functional-requirements.md](01-functional-requirements.md)
(see the "Exact text" column) and
[03-tray-icon-requirements.md](03-tray-icon-requirements.md) — this table
is an index, not the text's source of truth, so a developer without
`python/` access should follow those links rather than the `Source`
column, which is kept only for traceability back to the legacy
implementation.

| # | Screen | wx implementation | Source | Exact text | Wireframe |
|---|---|---|---|---|---|
| 1 | Tray icon (all visual states) | `wx.adv.TaskBarIcon` subclass `TaskBarIcon`, `set_icon()` | `mutagenmonlib/wx/icon.py` | [03-tray-icon-requirements.md §3](03-tray-icon-requirements.md) (full state/tooltip table) | [tray-icon-states.svg](wireframes/tray-icon-states.svg) |
| 2 | Tray right-click context menu | `TaskBarIcon.CreatePopupMenu()` | `mutagenmonlib/wx/icon.py` | [FR-7](01-functional-requirements.md#fr-7--tray-context-menu--session-control) | [tray-context-menu.svg](wireframes/tray-context-menu.svg) |
| 3 | Status view — no conflicts | `wx.MessageDialog` (OK / Information) via `on_left_down()` | `mutagenmonlib/wx/icon.py` | [FR-8](01-functional-requirements.md#fr-8--detailed-status-view); title = `TRAY_TOOLTIP`-derived current tooltip text | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) |
| 4 | Status view — with conflicts | `wx.MessageDialog` (OK/Cancel, custom labels "Resolve conflicts"/"Cancel", Question icon) via `on_left_down()` | `mutagenmonlib/wx/icon.py` | [FR-8](01-functional-requirements.md#fr-8--detailed-status-view) | [status-dialog-conflicts.svg](wireframes/status-dialog-conflicts.svg) |
| 5 | Conflict resolution chooser | `wx.SingleChoiceDialog` (radio list: Visual merge / A wins / B wins) via `resolve_single()` | `mutagenmonlib/remote/resolve.py` | [FR-9.1/FR-9.2](01-functional-requirements.md#fr-9--manual-conflict-resolution) (title format, body layout, `<N>`/`<total>` quirk) | [conflict-resolution-dialog.svg](wireframes/conflict-resolution-dialog.svg) |
| 6 | Transient "connecting" indicator | Small undecorated `wx.Dialog`, no buttons, auto-closed by code (`info_message()`) | `mutagenmonlib/wx/wx.py`, used throughout `resolve.py` | [FR-9.6](01-functional-requirements.md#fr-9--manual-conflict-resolution); title = the operation's description text, no body | [remote-connecting-toast.svg](wireframes/remote-connecting-toast.svg) |
| 7 | Merge-resolved confirmation | `wx.MessageDialog` (OK / Information) via `visual_merge()` | `mutagenmonlib/remote/resolve.py` | [FR-9.2](01-functional-requirements.md#fr-9--manual-conflict-resolution) — exact title/body given inline | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(same pattern, different text)* |
| 8 | Too-many-conflicts guard | `wx.MessageDialog` (OK / Information) via `resolve_all()` | `mutagenmonlib/remote/resolve.py` | [FR-9.5](01-functional-requirements.md#fr-9--manual-conflict-resolution) — exact title/body given inline | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(same pattern, different text)* |
| 9 | Duplicate session name warning | `wx.MessageDialog` (OK / Information) via `get_sessions()` | `mutagenmonlib/remote/mutagen.py` | [FR-1.2](01-functional-requirements.md#fr-1--session-configuration-loading) — exact title/body given inline | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(same pattern, different text)* |
| 10 | Fatal error dialog | `wx.MessageDialog` (OK / Error) via `errorBox()` | `mutagenmonlib/wx/wx.py`, invoked from the global exception hook and `run.py` | [FR-14.1](01-functional-requirements.md#fr-14--logging--diagnostics) — title always `"MutagenMon error"`, body = the exception/traceback text | [error-dialog.svg](wireframes/error-dialog.svg) |
| 11 | OS desktop notification (balloon/toast) | `TaskBarIcon.ShowBalloon()` falling back to `wx.adv.NotificationMessage` | `mutagenmonlib/wx/icon.py` (`notify()`) | [FR-11](01-functional-requirements.md#fr-11--desktop-notifications) — per-event title/toggle table | [notification-toast.svg](wireframes/notification-toast.svg) |
| 12 | Hidden root frame | `wx.Frame(None)`, created but never shown; exists only so the tray icon has an owner window | `mutagenmon.pyw` | — (never visible, no text) | *(not sketched — never visible)* |

## Notes for the rewrite

- Screens 3, 4, 7, 8, 9 all share one generic "message box" pattern
  (title, multi-line text, OK and optionally Cancel) — in the WPF app this
  should collapse to a single reusable dialog window parameterized by
  title/body/buttons, not five separate windows.
- Screen 6 (transient "connecting" indicator) has no buttons and is closed
  programmatically by the caller once the remote operation completes; it
  is not a screen the user dismisses.
- Screen 5 is the most structurally distinct screen (radio choice +
  structured two-column A/B comparison) and deserves its own component in
  the rewrite.
- None of these screens currently support resizing, theming, or
  accessibility beyond OS defaults (they are native wx.MessageDialog/
  SingleChoiceDialog instances) — the rewrite should at minimum match
  OS-native modal behavior (blocking, keyboard-dismissible, screen-reader
  visible title/text).
