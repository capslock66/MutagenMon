# User Acceptance Tests — .NET WPF rewrite

Manual, click-by-click test script to verify that each requirement in this
`requirements/` folder actually works in the running `dotNet/` application.
Each test is a short list of alternating **actions** (what you do) and
**expected results** (what you should observe) — follow them in order and
compare what you see against the expected-result lines.

- **Windows only**: the tray icon and every window described here only run
  on Windows (WPF). Start the app with
  `dotnet run --project src/MutagenMon.App` from `dotNet/`, or open
  `MutagenMon.sln` in Visual Studio, set **MutagenMon.App** as the startup
  project, and press F5 — see [dotNet/README.md](../dotNet/README.md).
- Every test references the requirement ID it verifies (e.g. `FR-9.2`,
  `TIC-7`) so a failure can be traced back to
  [01-functional-requirements.md](01-functional-requirements.md) or
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md).
- ✅ = implemented today, run this test. ⏳ = **not implemented yet** — do
  not run it, do not report it as a bug; it is listed here only so this
  document stays the complete, single reference against the full feature
  set.

## 0. Test environment setup

* Install a [mutagen.io](https://github.com/mutagen-io/mutagen) release
  and place the binary at `src/MutagenMon.App/mutagen/mutagen.exe` (or
  point `MutagenPath` in `config/config_mutagenmon.json` at it).
* Create two local folders for the sample session, e.g.
  `C:\MutagenMonTest\alpha` and `C:\MutagenMonTest\beta`, and edit
  `src/MutagenMon.App/mutagen/mutagen-create.bat` to reference them (the
  shipped sample already uses these two paths).
* Open `src/MutagenMon.App/config/config_mutagenmon.json` in a text
  editor — several tests below ask you to change a specific key there,
  then restart the app.
* Keep `log/mutagenMon.log` (or the folder named by `LogPath`) open in a
  viewer that auto-refreshes (e.g. PowerShell's
  `Get-Content mutagenMon.log -Wait -Tail 20`) so you can check log
  entries without stopping the app.
* Start the application. The tray icon should appear within a second or
  two.

## FR-1 — Session configuration loading

**UT-1.1 — Sessions are loaded from `mutagen-create.bat` (FR-1.1)** ✅

* Open `mutagen/mutagen-create.bat` and confirm it has one
  non-`rem `-prefixed `mutagen sync create ... --name=<name> ...` line per
  session you want monitored, plus at least one line starting with `rem `.
* Start MutagenMon.
* Right-click the tray icon.
* Click "Show status".
* A window is displayed with one "Name: / Status: / Alpha: / Beta:" block
  per active (non-`rem`) session line, and no block for the `rem`-prefixed
  line.

**UT-1.2 — Duplicate session names are detected and only one definition is
kept (FR-1.2)** ✅ *(log-only in this rewrite — see the note below)*

* Edit `mutagen-create.bat` and duplicate one `--name=<name>` line so the
  same name appears twice.
* Start MutagenMon.
* Open `log/mutagenMon.log`.
* A warning line "Duplicate session name in ...: `<name>`" is present.
* Right-click the tray icon.
* Click "Show status".
* `<name>` appears exactly once in the status view, not twice.
* Note: the legacy app shows a modal warning dialog at startup for
  this case; the current .NET rewrite only logs the warning — no popup is
  expected here yet.

**UT-1.3 — Config file tolerates `#` comment lines (FR-1.3)** ✅

* Open `config/config_mutagenmon.json` and confirm several lines start
  with `#`.
* Start MutagenMon.
* The tray icon appears normally.
* No startup error dialog is displayed.

## FR-2/FR-3/FR-4 — Polling, session classification, aggregated status

**UT-2.1 — Background polling runs on a fixed interval (FR-2.1)** ✅

* Start MutagenMon with at least one configured session.
* Open `log/mutagenMon.log` and watch it update.
* A new raw `mutagen sync list` output block is appended roughly once per
  second (`MutagenPollPeriodMs`, default 1000 ms).

**UT-2.2 — The aggregated status is the worst of all sessions (FR-4.1)** ✅

* Configure two sessions, both syncing normally.
* Produce a conflict in only one of them (see the setup in UT-9 below)
  while the other session stays "Watching for changes".
* Left-click the tray icon.
* A window is displayed showing one session as having a conflict and the
  other as healthy.
* The tray icon itself (before opening that window) shows the Conflicts
  icon, not the Ready icon — the worst session wins even though the other
  one is fine.

**UT-2.3 — Staging progress is parsed and logged (FR-2.2)** ✅

* Copy a large file (100+ MB, large enough that staging takes clearly
  longer than `MutagenPollPeriodMs`) into a session's alpha folder.
* While the transfer is in progress, watch `log/mutagenMon.log`.
* One Information-level line per poll appears: `Session '<name>' staging:
  <completed>/<total> files, <bytes> (<percent>%) — current file: <name>
  (<bytes>/<totalBytes>)`.
* Across consecutive polls, the byte counts increase.
* No such line appears for a session that's just "Watching for changes".
* Note: this is logged only for now — not yet shown in the tray tooltip
  or the status view.

## Tray icon (FR-5, TIC-1..10 — [03-tray-icon-requirements.md](03-tray-icon-requirements.md))

**UT-T.1 — Initial "waiting for status" icon (TIC-3)** ✅

* Start MutagenMon.
* Look at the tray icon immediately, before the first poll has completed.
* The icon is light gray (`lightgray-init`).
* Hover the mouse over the icon.
* The tooltip reads "MutagenMon: waiting for status...".

**UT-T.2 — Ready state**  ✅

* Wait until every configured session reaches "Watching for changes" in
  mutagen.
* Hover over the tray icon.
* The icon is plain green.
* The tooltip reads "MutagenMon: mutagen is watching for changes".

**UT-T.3 — Ready + "just updated" flash (tray icon §3, gap #2 fixed)** ✅

* With every session Ready, edit a file inside the alpha (or beta) test
  folder so mutagen syncs the change.
* Watch the tray icon during and right after the sync.
* The icon briefly switches to `green-success` with tooltip "MutagenMon:
  mutagen is watching for changes (updated)".
* A few seconds later, the icon returns to plain green ("Ready").

**UT-T.4 — Syncing state** ✅

* Copy a large-enough file into the alpha test folder that staging/
  applying takes a few seconds.
* Watch the tray icon while the transfer is in progress.
* The icon shows `green-sync`.
* The tooltip reads "MutagenMon: mutagen is syncing".

**UT-T.5 — Scanning state** ✅

* Right-click the tray icon.
* Click "Reload config & restart mutagen" so sessions rescan from
  scratch.
* Watch the tray icon while mutagen reports "Scanning files".
* The icon shows the `green-scan` placeholder icon (a generated stand-in,
  not a final design asset — see
  [03-tray-icon-requirements.md §3.1](03-tray-icon-requirements.md)).
* The tooltip reads "MutagenMon: mutagen is scanning".

**UT-T.6 — Conflicts state** ✅

* Produce a conflict (see the setup in UT-9 below).
* Hover over the tray icon.
* The icon shows `green-conflict`.
* The tooltip reads "MutagenMon: conflicts".

**UT-T.7 — Problems state** ✅

* Make mutagen unable to apply a change on one side (e.g. mark the target
  file read-only on one endpoint, then edit it on the other side).
* Hover over the tray icon.
* The icon shows `green-error`.
* The tooltip reads "MutagenMon: problems".

**UT-T.8 — Stopping monitoring changes the icon (FR-7.2 interaction)** ✅

* With a session Ready, right-click the tray icon.
* Click "Stop Mutagen sessions".
* The icon shows `green-stop`.
* The tooltip reads "MutagenMon: mutagen is stopping".
* Right-click the tray icon.
* Click "Start Mutagen sessions".
* The icon eventually reflects the session's real state again (note: the
  underlying stopped session is not itself relaunched by this toggle —
  re-enabling monitoring alone does not restart it. Either "Reload config &
  restart mutagen", a manual app restart, or the session's own FR-13
  abnormal-poll threshold being crossed again brings it back — see
  UT-13.1).

**UT-T.9 — Cannot connect / error state** ✅

* Edit `mutagen-create.bat` and point one session's remote endpoint at an
  unreachable host.
* Right-click the tray icon.
* Click "Reload config & restart mutagen" to apply the change.
* Wait until mutagen reports "Connecting to ..." (or "Waiting to
  connect") for a few consecutive polls.
* The icon shows `orange-restart`.
* The tooltip reads "MutagenMon: error (starting)".
* Right-click the tray icon.
* Click "Stop Mutagen sessions".
* The icon switches to `orange`.
* The tooltip reads "MutagenMon: error (disabled)".
* Restore the working endpoint in `mutagen-create.bat` and reload again
  afterwards.

**UT-T.10 — Staleness degrades the icon (FR-6.2)** ✅

* Rename `mutagen.exe` (or whatever `MutagenPath` points at) so polling
  starts failing.
* Watch the tray icon for the next couple of minutes without touching
  anything else.
* For the first ~4 seconds (`StatusMaxLag.Info`), the icon does not
  change yet.
* Between ~4s and ~15s, the icon shows `green-timeout-white` (a generated
  placeholder — see §3.1) with a "(stale)" tooltip suffix.
* Between ~15s and ~50s, the icon shows `green-timeout`.
* Between ~50s and ~90s, the icon shows `green-timeout-red` (also a
  placeholder).
* The tooltip keeps the last known state's own wording, e.g. "MutagenMon:
  mutagen is watching for changes (stale)", not a generic message.

**UT-T.11 — Restart threshold triggers a full self-restart (FR-6.3)** ✅

* Continue directly from UT-T.10, without restoring `mutagen.exe`.
* Wait until the staleness age passes 90 seconds
  (`StatusMaxLag.Restart`).
* The MutagenMon process restarts itself: the tray icon disappears and
  reappears, starting again from the "waiting for status" state
  (UT-T.1).
* `log/mutagenMon.log` contains a restart log entry.
* Restore `mutagen.exe`/`MutagenPath` afterwards.

**UT-T.12 — Left-click opens the status view (TIC-7)** ✅

* Left-click the tray icon.
* The detailed status view opens (see FR-8 tests below).

**UT-T.13 — Right-click opens the context menu (TIC-8)** ✅

* Right-click the tray icon.
* A menu is displayed with these options, top to bottom: "Reload config &
  restart mutagen", "Stop Mutagen sessions" (or "Start Mutagen sessions"
  if monitoring is currently off), a separator, "Show status", a
  separator, "Exit MutagenMon".

## FR-6 — Staleness detection & self-restart

Covered above by UT-T.10 and UT-T.11.

## FR-7 — Tray context menu & session control

**UT-7.1 — "Reload config & restart mutagen" (FR-7.1)** ✅

* Right-click the tray icon.
* Click "Reload config & restart mutagen".
* Right-click the tray icon again immediately.
* A menu is displayed with a single disabled "Restarting..." item and
  "Exit MutagenMon" only — the other items are gone.
* Wait a few seconds.
* The process restarts: the tray icon disappears and reappears.
* `log/mutagenMon.log` records the restart.
* Right-click the tray icon once it is back.
* The full menu (Reload / Stop-Start / Show status / Exit) is displayed
  again.

**UT-7.2 — "Stop Mutagen sessions" / "Start Mutagen sessions" (FR-7.2)** ✅

* Right-click the tray icon.
* Click "Stop Mutagen sessions".
* Every running session is terminated (verify with `mutagen sync list` in
  a terminal, or via "Show status").
* Right-click the tray icon.
* The item now reads "Start Mutagen sessions".
* Click "Start Mutagen sessions".
* Right-click the tray icon.
* The item reads "Stop Mutagen sessions" again (note: the previously
  stopped session is not itself relaunched by this action alone — see
  UT-T.8).

**UT-7.3 — "Show status" (FR-7.3)** ✅

* Right-click the tray icon.
* Click "Show status".
* The same detailed status view as a left-click (FR-8) is displayed.

**UT-7.4 — "Exit MutagenMon" (FR-7.4)** ✅

* Right-click the tray icon.
* Click "Exit MutagenMon".
* The tray icon disappears immediately.
* Open Task Manager.
* The MutagenMon process is no longer running, and no new instance
  starts on its own.

**UT-7.5 — Menu collapses to "Restarting..." during a restart (FR-7.5)**

Covered above by UT-7.1.

## FR-8 — Detailed status view

**UT-8.1 — Status view with no conflicts (FR-8.1/FR-8.3)** ✅

* With every session healthy and no conflicts, left-click the tray icon.
* A window is displayed. Its title bar reads plain "MutagenMon" — there is
  no separate header line above the grid.
* The window's content shows a grid with one row per configured session,
  columns Name / Status / Alpha / Beta / Last changed. Session identifiers
  are never shown. Each row's Status cell shows a small status icon (the
  same icon that would be shown in the system tray if this were the only/
  worst session) to the left of the status text, e.g. green for "Watching
  for changes". "Last changed" reads "—" for a session that hasn't synced
  anything since the app started, or a timestamp otherwise.
* The window can be resized (drag an edge/corner) and the grid grows/
  shrinks with it.
* Only an "OK" button is displayed — no "Cancel", no "Resolve conflicts".
* Click "OK".
* The window closes.

**UT-8.2 — Status view with unresolved conflicts (FR-8.1/FR-8.2)** ✅

* Produce at least one unresolved conflict (see UT-9 setup below).
* Left-click the tray icon.
* The window's content also shows a
  "==================== CONFLICTS ====================" section listing
  "`<session>: <file>`" for the conflicting file (with an
  "`[autoresolving]`" suffix instead if an `AutoResolve` rule matches it
  — see FR-10 below).
* Both a "Cancel" button and a "Resolve conflicts" button are displayed —
  no plain "OK".
* Click "Cancel".
* The window closes without starting conflict resolution.

**UT-8.3 — Status view stays live while open (FR-8.4)** ✅

* With every session healthy and no conflicts, left-click the tray icon to
  open the status view, and leave it open (don't click OK/Cancel).
* Without closing the window, produce a conflict (see the FR-9 setup
  below: stop sessions, edit the same file differently on both sides,
  restart sessions) or otherwise change a session's status.
* Within about a second, the already-open window updates on its own: the
  "CONFLICTS" section (and Cancel/"Resolve conflicts" buttons) appear
  without the window being closed and reopened.
* Likewise, editing/syncing a file in a healthy session updates that
  session's "Last changed" column and its row's status icon within a poll
  or two, without closing the window. Rows for sessions whose data hasn't
  changed don't visibly flicker/redraw (FR-8.4's no-op-when-unchanged
  behavior applies per row, not just to the window as a whole).

**UT-8.4 — Upload progress shown while staging a large file (FR-2.2/FR-8.1)** ✅

* Change a large file (at least a few hundred MB, so staging takes more
  than a couple of polls) on one side of a healthy session.
* Open the status view while the session is actively staging that file to
  the other side.
* Instead of the static "Staging files on alpha"/"Staging files on beta"
  text, the Status column shows "Uploading `<n>`/`<total>` ,
  `<file name>` , `<bytes transferred>`" (e.g. "Uploading 1/2 ,
  Tracetool.zip , 237 MB"), and both `<n>` and `<bytes transferred>`
  advance on subsequent polls without closing/reopening the window
  (FR-8.4).
* `<n>` counts the file currently in flight, not files already finished:
  it starts at 1 (not 0) as soon as the first file begins uploading, and
  only reaches `<total>` while the last file is transferring.
* Once staging finishes, the Status column reverts to the normal status
  text (e.g. "Watching for changes").

## FR-9 — Manual conflict resolution

**Setup used by every test below**: to produce a real conflict, right-click
the tray icon and click "Stop Mutagen sessions"; edit the *same* file with
different content directly in the alpha folder and in the beta folder (or
its remote equivalent); then click "Start Mutagen sessions" again. Mutagen
detects this as a two-sided edit and reports a conflict on its next poll.

**UT-9.1 — Conflict batch entry and A/B comparison (FR-9.1)** ✅

* Produce one conflict (see setup above).
* Left-click the tray icon.
* Click "Resolve conflicts".
* A window is displayed with the title "MutagenMon: resolve file conflict
  1 of 1" (or "N of total" if several conflicts are pending).
* The content shows the conflicting file's name, and for each of A and B:
  the endpoint URL, the file size in bytes, and the last-modified
  timestamp.

**UT-9.2 — Default choice follows the most recently modified side
(FR-9.3)** ✅

* In the window from UT-9.1, note which radio button, "A wins" or "B
  wins", is pre-selected.
* Compare the two timestamps shown above it.
* The pre-selected option matches whichever side (A or B) has the more
  recent timestamp.

**UT-9.3 — "A wins" resolution (FR-9.2)** ✅

* With the conflict dialog open, click the "A wins" radio button.
* Click "OK".
* B's copy of the file now has A's content (compare the two files
  directly).
* Open `log/mutagenMon.log`.
* A new "Conflict resolved" line is present with the session name, both
  URLs, the file name, method "A wins", and no "[AUTO]" tag.

**UT-9.4 — "B wins" resolution (FR-9.2)** ✅

* Produce a fresh conflict (see setup above).
* Open the resolution dialog and click the "B wins" radio button.
* Click "OK".
* A's copy of the file now has B's content.
* `log/mutagenMon.log` has a new "Conflict resolved" line with method "B
  wins".

**UT-9.5 — Visual merge resolution (FR-9.2)** ✅

* Set `MergePath` in `config_mutagenmon.json` to a real merge tool (e.g.
  WinMerge) and restart MutagenMon.
* Produce a conflict and open the resolution dialog.
* Click the "Visual merge" radio button.
* Click "OK".
* The configured merge tool opens with local copies of both A and B.
* Edit and save the left (A) pane in the merge tool.
* Close the merge tool.
* A confirmation window is displayed, titled "MutagenMon: resolved file
  conflict", with the content "Merged file copied to both sides:"
  followed by the file name.
* Click "OK".
* Both A and B now contain the merged content.

**UT-9.6 — Visual merge re-prompts when nothing changed (FR-9.2)** ✅

* Repeat UT-9.5, but close the merge tool without changing either pane.
* No confirmation window is displayed.
* The same conflict is presented again immediately, instead of silently
  moving to the next one.

**UT-9.7 — Cancelling on one conflict (FR-9.4)** ✅ *(tests current
behavior, which is now known to diverge from corrected FR-9.4 — see below)*

* Produce two separate conflicts.
* Left-click the tray icon.
* Click "Resolve conflicts".
* On the first conflict presented, click "Cancel".
* **Current .NET behavior**: no further conflict window is displayed (the
  whole batch is aborted) and neither file was modified.
* **Corrected FR-9.4 (true legacy behavior)**: Cancel should only skip the
  first conflict and immediately present the second one, not abort the
  batch. The current .NET implementation was built against this
  document's previous, incorrect wording of FR-9.4, not against the
  legacy app's actual behavior — see the discrepancy note under FR-9.4 in
  [01-functional-requirements.md](01-functional-requirements.md#fr-9--manual-conflict-resolution).
  This test should be rewritten (and the code fixed, if legacy parity is
  wanted) rather than left as documenting the divergent behavior.

**UT-9.8 — Too-many-conflicts guard (FR-9.5)** ✅ *(needs 100+ conflicts —
optional if you can't produce that many)*

* Produce more than 100 unresolved conflicts.
* Left-click the tray icon.
* Click "Resolve conflicts".
* A window is displayed with the title "MutagenMon: resolve file
  conflict" and the content "Too many conflicts. You can restart
  resolving or resolve manually" (no trailing period) instead of the usual
  A/B comparison.

**UT-9.9 — "Connecting..." indicator for remote endpoints (FR-9.6)** ✅

* Produce a conflict where at least one side is an SSH endpoint.
* Left-click the tray icon.
* Click "Resolve conflicts".
* A small, borderless window with the text "Remote connection..." is
  briefly displayed while file sizes/timestamps are fetched.
* The window disappears on its own once the comparison window (UT-9.1)
  appears — it is never dismissed by the user.

**UT-9.10 — Directory-level conflict (FR-9.2, not covered by the original
FR-9 wording)** ✅

* Produce a conflict where mutagen reports a whole directory rather than a
  single file (e.g. delete a synced subdirectory on one side while new
  untracked content appears under it on the other — `mutagen sync list -l`
  shows a line like `(alpha) some/dir (Directory -> <non-existent>)`).
* Left-click the tray icon, click "Resolve conflicts".
* The dialog shows `(directory)` (not a byte size) for whichever side is a
  directory, and `(does not exist)` for a side that has none of the entry.
* The "Visual merge" option is disabled/greyed out (a directory can't be
  diffed with the merge tool).
* Choosing "A wins"/"B wins" and clicking "OK" replaces the destination
  side's contents at that path with an exact copy of the winning side's
  subtree (recursively), or deletes the destination entirely when the
  winning side no longer has the entry.
* `log/mutagenMon.log` gets a new "Conflict resolved" line as usual.

## FR-10 — Automatic conflict resolution

**UT-10.1 — A matching rule auto-resolves without any prompt (FR-10.1/
FR-10.2)** ✅

* Stop MutagenMon.
* Edit `config/config_mutagenmon.json` and set:
  `"AutoResolve": [{"filepath": "auto-resolve-test", "resolve": "A wins"}]`
* Start MutagenMon.
* Produce a conflict (see the FR-9 setup) on a file whose name contains
  `auto-resolve-test`.
* Wait one poll cycle (~1 second).
* No conflict-resolution window is displayed.
* B's copy of the file is automatically overwritten with A's content.
* Open `log/mutagenMon.log`.
* A new "Conflict resolved" line is present with method "A wins" and the
  "[AUTO]" tag.

**UT-10.2 — First matching rule wins (FR-10.1)** ✅

* Stop MutagenMon.
* Edit `config/config_mutagenmon.json` and set:
  `"AutoResolve": [{"filepath": "auto-resolve-test", "resolve": "A wins"}, {"filepath": "auto-resolve-test", "resolve": "B wins"}]`
* Start MutagenMon and produce the same conflict as UT-10.1.
* B's copy is overwritten with A's content (the first rule wins), even
  though the second rule also matches the file name.

**UT-10.3 — Conflict excluded from the manual workflow once auto-resolved
(FR-10.2)** ✅

* With the rule from UT-10.1 active, produce a matching conflict.
* Left-click the tray icon.
* If the CONFLICTS section is still visible at all, the entry is
  annotated "`[autoresolving]`"; more commonly it has already disappeared
  from the list entirely.
* Click "Show status" again (or reopen it) if a second, unrelated
  conflict is still pending.
* The auto-resolved file never appears in the "Resolve conflicts" manual
  batch (UT-9.1).

**UT-10.4 — Grace period prevents reprocessing the same conflict
(FR-10.3)** ✅

* With the rule from UT-10.1 active and `AutoResolveHistoryAgeSeconds` at its
  default (30 seconds), let UT-10.1 run once.
* Note the timestamp of the resulting "Conflict resolved" line in
  `log/mutagenMon.log`.
* Without changing the file again, wait less than 30 seconds and check
  `mutagenMon.log` again.
* No second "Conflict resolved" line has appeared for the same
  session/file — the grace period is preventing reprocessing.
* Wait until more than 30 seconds have passed since the first entry, then
  modify the conflicting file identically on both sides again.
* A new "Conflict resolved" line appears for that session/file once the
  grace period has elapsed.

## FR-11 — Desktop notifications

FR-11.3 and FR-11.4 are called out separately below — their trigger points
don't exist yet in this codebase. FR-11.1 and FR-11.2 are fully
implemented.

**UT-11.1 — New conflict raises a toast notification (FR-11.1)** ✅

* Ensure `"NotifyConflicts": true` in `config/config_mutagenmon.json`.
* Produce a conflict (see the FR-9 setup).
* Within one poll cycle (~1 second) a non-modal toast notification titled
  "New conflicts" appears near the tray icon, naming the session and file
  (`session:file`), then disappears on its own.

**UT-11.2 — The same conflict is not renotified every poll (FR-11.1)** ✅

* With the conflict from UT-11.1 still unresolved, wait a few more poll
  cycles.
* No further "New conflicts" toast appears for that same session/file
  while it stays unresolved.
* Resolve it (manually or otherwise) until the tray icon returns to Ready,
  then produce the exact same conflict again.
* A new "New conflicts" toast appears — the conflict is treated as new
  again once everything had gone back to Ready in between.

**UT-11.3 — Auto-resolved conflicts are excluded from the "new conflicts"
toast (FR-10.2/FR-11.1)** ✅

* With an `AutoResolve` rule configured to match a file (see UT-10.1),
  produce a matching conflict.
* No "New conflicts" toast appears for that file — it goes straight to the
  auto-resolve notification below instead.

**UT-11.4 — Auto-resolve notification names the rule and the file
(FR-11.2/FR-10.4)** ✅

* Ensure `"NotifyAutoresolve": true` in `config/config_mutagenmon.json`.
* Produce a conflict matching an `AutoResolve` rule (see UT-10.1).
* Within one poll cycle a toast titled "Conflict auto-resolved" appears,
  naming the session, file, and the resolution applied (e.g. "A wins").

**UT-11.5 — Each notification type is independently toggleable (FR-11)**
✅

* Set `"NotifyConflicts": false` and `"NotifyAutoresolve": true`, restart
  MutagenMon, and produce a plain (non-auto-resolved) conflict.
* No "New conflicts" toast appears.
* With the same config, produce a conflict matching an `AutoResolve` rule.
* The "Conflict auto-resolved" toast still appears — the two toggles are
  independent.

**UT-11.6 — Stuck-connection-restart notification (FR-11.3)** ✅

* Ensure `"NotifyRestartConnection": true` and a small
  `SessionMaxErrors` (e.g. `2`) in `config/config_mutagenmon.json`.
* Force a session to stay in a "Connecting to..."/"Waiting to
  connect..."/"Unknown" status for more than `SessionMaxErrors`
  consecutive polls (e.g. block the remote endpoint).
* The session is restarted (terminate + recreate — see UT-13.3) and a
  toast titled with the session name appears, body
  `"Restarting connection: <status>"`.
* Set `"NotifyRestartConnection": false`, repeat — the session still
  restarts, but no toast appears.

**UT-11.7 — Duplicate-restart notification, always on (FR-11.3b)** ✅

* Set `"NotifyRestartConnection": false` (deliberately, to prove this
  notification is not gated by it) and a small `SessionMaxDuplicate`
  (e.g. `2`) in `config/config_mutagenmon.json`.
* Produce a duplicate session name for more than `SessionMaxDuplicate`
  consecutive polls (see UT-13.2 for how the restart itself is verified).
* A toast titled with the session name appears, body
  `"Restarting duplicate: <status>"`, even though
  `NotifyRestartConnection` is disabled — this notification has no
  toggle of its own and always fires.

**UT-11.8 — No-session restart never notifies (FR-11.3c)** ✅

* With `"NotifyRestartConnection": true` and a small
  `SessionMaxNoSession` (e.g. `2`), make a session disappear from
  `mutagen sync list` entirely for more than `SessionMaxNoSession`
  consecutive polls (see UT-13.1).
* The session is restarted, but no toast appears for it at all — this is
  the one restart cause that is silent by design, unconditionally.

**UT-11.9 — Profile-update notification (FR-11.4)** ✅

* Ensure `"NotifyMutagenProfileUpdate": true` in
  `config/config_mutagenmon.json`.
* Trigger a real file sync on a monitored session (e.g. save a file inside
  a synced folder) and wait past `MutagenProfileGraceSeconds` seconds (default
  4s) without further writes.
* A toast titled "Updated" appears, naming the session.
* See UT-12.2 below for the debounce behavior that gates this toast.

## FR-12 — Session profile change detection

**UT-12.1 — Tray icon "updated" flash on archive change (FR-12.1/
FR-12.3)** ✅

Covered above by UT-T.3 — the "updated" icon flash is this requirement's
only user-visible tray effect today.

**UT-12.2 — Debounced profile-update notification (FR-12.2, gates
FR-11.4)** ✅

* Ensure `"NotifyMutagenProfileUpdate": true` and a short
  `MutagenProfileGraceSeconds` (e.g. `4`) in `config/config_mutagenmon.json`.
* Trigger several rapid successive writes on a monitored session within
  less than the grace period — no "Updated" toast appears yet (each write
  keeps pushing the archive mtime forward, so the debounce window never
  elapses).
* Stop writing and wait past the grace period.
* Exactly one "Updated" toast appears for that session, not one per write.

## FR-13 — Automatic session recovery

**UT-13.1 — No-result restart (FR-13.1)** ✅

* Set a small `SessionMaxNoSession` (e.g. `2`) in
  `config/config_mutagenmon.json` and restart MutagenMon.
* Make a monitored session disappear from `mutagen sync list` entirely
  (e.g. `mutagen sync terminate <name>` from a separate shell, without
  going through MutagenMon) for more than `SessionMaxNoSession`
  consecutive polls.
* MutagenMon recreates the session on its own (`mutagen sync list` shows
  it again shortly after) — no user action needed.
* No "New conflicts"/restart toast appears for this session (see UT-11.8).
* `log/mutagenMon.log` gets a new Warning-level line with the raw status
  snapshot and `Restarting: <name>`.

**UT-13.2 — Duplicate-name restart (FR-13.2)** ✅

* Set a small `SessionMaxDuplicate` (e.g. `2`).
* Produce a duplicate session name (e.g. two sessions sharing the same
  `--name` in `mutagen/mutagen-create.bat`, or manually
  `mutagen sync create` a session with a name MutagenMon already manages)
  for more than `SessionMaxDuplicate` consecutive polls.
* The session is restarted and a toast appears (see UT-11.7).
* `log/mutagenMon.log` gets a line with `Restarting duplicate: <name>`.

**UT-13.3 — Stuck-connecting restart (FR-13.3)** ✅

* Set a small `SessionMaxErrors` (e.g. `2`).
* Force a session's status to stay "Connecting to..."/"Waiting to
  connect..."/"Unknown" for more than `SessionMaxErrors` consecutive
  polls (e.g. make the remote endpoint unreachable).
* The session is restarted; whether a toast appears depends on
  `NotifyRestartConnection` (see UT-11.6).
* `log/mutagenMon.log` gets a line with `Restarting connection: <name>`.

**UT-13.4 — Restart is terminate-then-recreate, each step independent
(FR-13.5)** ✅

* Trigger UT-13.2 (duplicate) or UT-13.3 (stuck-connecting) while the
  session is already gone from `mutagen`'s own session list (so the
  termination step itself fails — the session is known to exist per its
  status, so termination is still attempted).
* MutagenMon still attempts to recreate the session afterwards — check
  `mutagen sync list` shows it again, and `log/mutagenMon.log` has a
  warning for the failed termination but the session is still running.

**UT-13.4b — No-session restart skips the termination step
(FR-13.5)** ✅

* Trigger UT-13.1 (no-result restart).
* `log/mutagenMon.log` has **no** "Failed to terminate session" warning
  for this session — the termination step was skipped entirely, since the
  session was already known to be absent (as opposed to UT-13.4, where
  termination is attempted and its failure tolerated).
* MutagenMon still recreates the session — `mutagen sync list` shows it
  again.

**UT-13.5 — Disabled monitoring never auto-restarts (FR-13.6)** ✅

* From the tray menu, choose "Stop Mutagen sessions" (FR-7.2).
* Leave a session in an abnormal state (or let it disappear) well past its
  configured threshold.
* No automatic restart happens — the session stays terminated, matching
  UT-7.2's "stop, don't restart while disabled" behavior, until monitoring
  is re-enabled.

## FR-14 — Logging & diagnostics

**UT-14.1 — An unhandled exception is logged and shown to the user
(FR-14.1)** ✅ *(the deliberate "Boum" test button that used to make this
repro trivial has been removed from the status view — this now needs a
real unhandled exception, e.g. any of the FR-9 conflict-resolution crashes
covered elsewhere in this document)*

* Trigger an unhandled exception on the UI thread.
* A window is displayed, titled "MutagenMon — error", with the content
  "MutagenMon hit an unexpected error:" followed by exception details.
* Click "OK".
* The application stays open (deliberate: a UI-thread exception is logged
  and shown, but no longer treated as fatal — only a background-thread/
  process-wide failure restarts or exits the app, per FR-13/FR-6).
* `log/mutagenMon.log` contains a Critical-level entry with the same
  exception.
* Windows Event Viewer → Windows Logs → Application also has a matching
  `Error` entry from source "MutagenMon" — every Critical entry reaches
  the Windows Event Log, not just ones logged before config is loaded.

**UT-14.2 — A startup failure is logged and shown (FR-14.1)** ✅

* Stop MutagenMon.
* Rename `config/config_mutagenmon.json` (or edit it so the JSON is
  invalid) so config loading fails.
* Delete/rename any pre-existing `<BaseDirectory>/log` folder so you can
  tell whether this run creates one.
* Start MutagenMon.
* A window is displayed, titled "MutagenMon — startup error", with the
  content "MutagenMon failed to start:" followed by exception details.
* No `<BaseDirectory>/log` folder is (re)created — config's `LogPath`
  was never read, and there is deliberately no default/fallback path
  under the app's own directory for this window, nor any fallback file
  next to the executable (see FR-14.1's rewrite note).
* Open Windows Event Viewer → Windows Logs → Application. An `Error`
  entry from source "MutagenMon" is present, with the same exception
  text — the only durable trace of this failure.
* Restore the config file afterwards.

**UT-14.3 — Conflict resolutions are logged to the unified log (FR-9.7)** ✅

Covered above by UT-9.3/UT-9.4/UT-10.1 — every resolution is logged to
`mutagenMon.log`. There is no separate `resolve.log` any more (see
FR-14.3's rewrite note).

**UT-14.4 — Automatic restarts are logged to the unified log (FR-13.4)** ✅

Covered above by UT-13.1/UT-13.2/UT-13.3 — every FR-13 automatic restart
is logged to `mutagenMon.log`. There is no separate `restart.log` any more
(see FR-14.3's rewrite note). Note: the *whole-app* self-restart mechanism
(staleness watchdog, FR-6 — see UT-T.11) is a separate, unrelated
mechanism that has always logged to `mutagenMon.log` only — this entry is
specifically about FR-13's per-session restarts, which used to go to a
dedicated file and no longer do.

**UT-14.5 — Verbosity gate (FR-14.2)** ✅

* Stop MutagenMon.
* Set `"MinLogLevel": "Warning"` in `config/config_mutagenmon.json`.
* Start MutagenMon and let it run through a normal startup and a few poll
  cycles.
* Open `log/mutagenMon.log` (or wherever `LogPath` points).
* The file's very first line is already "Configuration loaded: ..." (or
  later) — **not** "MutagenMon starting..." or "Loading configuration
  from...": those two lines are logged before config (and therefore
  `LogPath` itself) is known, so they're never persisted to this file at
  all (see FR-14.1's rewrite note) — nothing to filter by level, there
  simply is no file yet at that point.
* No `[INF]`/`[DBG]`-tagged lines appear anywhere in the file (no routine
  "tray icon changed", "poll" breadcrumbs, etc.) — only
  `[WRN]`/`[ERR]`/`[FTL]` lines, if any actually occur.
* Set `"MinLogLevel": "Trace"` (or remove the key), restart, and confirm
  `[INF]`/`[DBG]` lines resume appearing throughout (still starting from
  "Configuration loaded: ...", not from "MutagenMon starting...").
* Note: `DebugLevel` (the legacy 0-100 dial) has no effect — `MinLogLevel`
  is the key that actually controls verbosity in the rewrite.

**UT-14.6 — Every menu/button action is logged as "User action: ..."
(FR-14.4)** ✅

* Left-click the tray icon (or use "Show status" from the context menu) to
  open the status window.
* `log/mutagenMon.log` contains `User action: show status clicked`.
* From the tray context menu, click "Reload config & restart mutagen".
* The log contains `User action: reload config & restart mutagen
  requested`.
* From the tray context menu, click "Stop Mutagen sessions" (or "Start
  Mutagen sessions").
* The log contains `User action: toggling monitoring to True` (or
  `False`).
* In the status window, click "OK" or "Cancel" — the log contains `User
  action: status window OK clicked` or `User action: status window Cancel
  clicked` respectively.
* Trigger at least one conflict, open the status window, and click
  "Resolve conflicts" — the log contains `User action: resolve conflicts
  clicked`.
* In the resulting conflict resolution dialog, click "OK" — the log
  contains `User action: conflict resolution OK clicked (<Choice>)` with
  the chosen resolution; clicking "Cancel" instead logs `User action:
  conflict resolution Cancel clicked`.
* Trigger a "Too many conflicts" or "resolved file conflict" informational
  dialog (`GenericMessageDialog`) and click "OK" (or "Cancel" when
  offered) — the log contains `User action: <dialog title> OK clicked` (or
  `Cancel clicked`).
* From the tray context menu, click "Exit MutagenMon".
* The log contains `User action: exit requested; shutting down`.

## FR-15 — Single, always-on background operation

**UT-15.1 — No main window is ever shown (FR-15.1)** ✅

* Start MutagenMon normally.
* Do not click the tray icon.
* Check the taskbar and Alt-Tab.
* No application window is displayed anywhere — only the tray icon is
  visible.

**UT-15.2 — Clean shutdown on Exit (FR-15.2)** ✅

Covered above by UT-7.4.

## Appendix — known, accepted gaps (do not report as bugs)

These are documented, intentional limitations of the current phase, not
defects:

* The three "generated placeholder" tray icons (`green-scan`,
  `green-timeout-white`, `green-timeout-red`, seen in UT-T.5/UT-T.10) are
  simple colored-circle stand-ins, not final design assets — see
  [03-tray-icon-requirements.md §3.1/§7.1](03-tray-icon-requirements.md).
* Duplicate session names (UT-1.2) are only logged, not shown in a popup
  — a deliberate, currently-accepted deviation from the legacy app.
* FR-11.1/FR-11.2/FR-11.3/FR-11.4 (new-conflict, auto-resolve,
  automatic-restart, and profile-update notifications), FR-12 (session
  profile change detection, including the FR-12.2 debounce), and FR-13
  (automatic session recovery, logged to the unified `mutagenMon.log` per
  FR-13.4) are all implemented. Only FR-14.2 (verbosity gate) remains
  deliberately unimplemented — see the ⏳ section above.
