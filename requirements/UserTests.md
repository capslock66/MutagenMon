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
  document stays the complete, single reference as later phases land (see
  [05-wpf-migration-notes.md §6](05-wpf-migration-notes.md#6-suggested-phased-delivery)
  for what's planned next).

## 0. Test environment setup

* Install a [mutagen.io](https://github.com/mutagen-io/mutagen) release
  and place the binary at `src/MutagenMon.App/mutagen/mutagen.exe` (or
  point `MUTAGEN_PATH` in `config/config_mutagenmon.json` at it).
* Create two local folders for the sample session, e.g.
  `C:\MutagenMonTest\alpha` and `C:\MutagenMonTest\beta`, and edit
  `src/MutagenMon.App/mutagen/mutagen-create.bat` to reference them (the
  shipped sample already uses these two paths).
* Open `src/MutagenMon.App/config/config_mutagenmon.json` in a text
  editor — several tests below ask you to change a specific key there,
  then restart the app.
* Keep `log/mutagenMon.log` (or the folder named by `LOG_PATH`) open in a
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
* Note: the legacy Python app shows a modal warning dialog at startup for
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
  second (`MUTAGEN_POLL_PERIOD`, default 1000 ms).

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
  only "Reload config & restart mutagen", or a manual app restart, brings
  it back; automatic recovery is FR-13, not implemented yet).

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

* Rename `mutagen.exe` (or whatever `MUTAGEN_PATH` points at) so polling
  starts failing.
* Watch the tray icon for the next couple of minutes without touching
  anything else.
* For the first ~4 seconds (`STATUS_MAX_LAG.Info`), the icon does not
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
  (`STATUS_MAX_LAG.Restart`).
* The MutagenMon process restarts itself: the tray icon disappears and
  reappears, starting again from the "waiting for status" state
  (UT-T.1).
* `log/mutagenMon.log` contains a restart log entry.
* Restore `mutagen.exe`/`MUTAGEN_PATH` afterwards.

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
* A window is displayed, titled with the current tray tooltip (e.g.
  "MutagenMon: mutagen is watching for changes").
* The window's content shows one "Name: / Status: / Alpha: / Beta:" block
  per configured session.
* Only an "OK" button is displayed — no "Cancel", no "Resolve conflicts".
* Click "OK".
* The window closes.

**UT-8.2 — Status view with unresolved conflicts (FR-8.1/FR-8.2)** ✅

* Produce at least one unresolved conflict (see UT-9 setup below).
* Left-click the tray icon.
* The window's content also shows a
  "==================== CONFLICTS ====================" section listing
  "`<session>: <file>`" for the conflicting file (with an
  "`[autoresolving]`" suffix instead if an `AUTORESOLVE` rule matches it
  — see FR-10 below).
* Both a "Cancel" button and a "Resolve conflicts" button are displayed —
  no plain "OK".
* Click "Cancel".
* The window closes without starting conflict resolution.

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
* Open `log/resolve.log`.
* A new entry is present with the session name, both URLs, the file
  name, method "A wins", and no "[AUTO]" tag.

**UT-9.4 — "B wins" resolution (FR-9.2)** ✅

* Produce a fresh conflict (see setup above).
* Open the resolution dialog and click the "B wins" radio button.
* Click "OK".
* A's copy of the file now has B's content.
* `log/resolve.log` has a new entry with method "B wins".

**UT-9.5 — Visual merge resolution (FR-9.2)** ✅

* Set `MERGE_PATH` in `config_mutagenmon.json` to a real merge tool (e.g.
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

**UT-9.7 — Cancelling aborts the whole batch (FR-9.4)** ✅

* Produce two separate conflicts.
* Left-click the tray icon.
* Click "Resolve conflicts".
* On the first conflict presented, click "Cancel".
* No further conflict window is displayed.
* Neither file was modified.

**UT-9.8 — Too-many-conflicts guard (FR-9.5)** ✅ *(needs 100+ conflicts —
optional if you can't produce that many)*

* Produce more than 100 unresolved conflicts.
* Left-click the tray icon.
* Click "Resolve conflicts".
* A window is displayed with the title "MutagenMon: resolve file
  conflict" and the content "Too many conflicts. You can restart
  resolving or resolve manually." instead of the usual A/B comparison.

**UT-9.9 — "Connecting..." indicator for remote endpoints (FR-9.6)** ✅

* Produce a conflict where at least one side is an SSH endpoint.
* Left-click the tray icon.
* Click "Resolve conflicts".
* A small, borderless window with the text "Remote connection..." is
  briefly displayed while file sizes/timestamps are fetched.
* The window disappears on its own once the comparison window (UT-9.1)
  appears — it is never dismissed by the user.

## FR-10 — Automatic conflict resolution

**UT-10.1 — A matching rule auto-resolves without any prompt (FR-10.1/
FR-10.2)** ✅

* Stop MutagenMon.
* Edit `config/config_mutagenmon.json` and set:
  `"AUTORESOLVE": [{"filepath": "auto-resolve-test", "resolve": "A wins"}]`
* Start MutagenMon.
* Produce a conflict (see the FR-9 setup) on a file whose name contains
  `auto-resolve-test`.
* Wait one poll cycle (~1 second).
* No conflict-resolution window is displayed.
* B's copy of the file is automatically overwritten with A's content.
* Open `log/resolve.log`.
* A new entry is present with method "A wins" and the "[AUTO]" tag.

**UT-10.2 — First matching rule wins (FR-10.1)** ✅

* Stop MutagenMon.
* Edit `config/config_mutagenmon.json` and set:
  `"AUTORESOLVE": [{"filepath": "auto-resolve-test", "resolve": "A wins"}, {"filepath": "auto-resolve-test", "resolve": "B wins"}]`
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

* With the rule from UT-10.1 active and `AUTORESOLVE_HISTORY_AGE` at its
  default (30 seconds), let UT-10.1 run once.
* Note the timestamp of the resulting `resolve.log` entry.
* Without changing the file again, wait less than 30 seconds and check
  `resolve.log` again.
* No second entry has appeared for the same session/file — the grace
  period is preventing reprocessing.
* Wait until more than 30 seconds have passed since the first entry, then
  modify the conflicting file identically on both sides again.
* A new `resolve.log` entry appears for that session/file once the grace
  period has elapsed.

## FR-11 — Desktop notifications

FR-11.3 and FR-11.4 are called out separately below — their trigger points
don't exist yet in this codebase (see
[05-wpf-migration-notes.md §6](05-wpf-migration-notes.md#6-suggested-phased-delivery)).
FR-11.1 and FR-11.2 are fully implemented.

**UT-11.1 — New conflict raises a toast notification (FR-11.1)** ✅

* Ensure `"NOTIFY_CONFLICTS": true` in `config/config_mutagenmon.json`.
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

* With an `AUTORESOLVE` rule configured to match a file (see UT-10.1),
  produce a matching conflict.
* No "New conflicts" toast appears for that file — it goes straight to the
  auto-resolve notification below instead.

**UT-11.4 — Auto-resolve notification names the rule and the file
(FR-11.2/FR-10.4)** ✅

* Ensure `"NOTIFY_AUTORESOLVE": true` in `config/config_mutagenmon.json`.
* Produce a conflict matching an `AUTORESOLVE` rule (see UT-10.1).
* Within one poll cycle a toast titled "Conflict auto-resolved" appears,
  naming the session, file, and the resolution applied (e.g. "A wins").

**UT-11.5 — Each notification type is independently toggleable (FR-11)**
✅

* Set `"NOTIFY_CONFLICTS": false` and `"NOTIFY_AUTORESOLVE": true`, restart
  MutagenMon, and produce a plain (non-auto-resolved) conflict.
* No "New conflicts" toast appears.
* With the same config, produce a conflict matching an `AUTORESOLVE` rule.
* The "Conflict auto-resolved" toast still appears — the two toggles are
  independent.

**UT-11.6 — Stuck-connection-restart notification (FR-11.3) ⏳ NOT
IMPLEMENTED YET**

No test steps — moved to
[05-wpf-migration-notes.md §6, Phase 5](05-wpf-migration-notes.md#6-suggested-phased-delivery)
together with FR-13, whose per-session restart-on-connecting-threshold
logic this notification depends on and which doesn't exist yet. The
`NOTIFY_RESTART_CONNECTION` config toggle exists but is not read anywhere.

**UT-11.7 — Profile-update notification (FR-11.4) ⏳ NOT IMPLEMENTED YET**

No test steps — see UT-12.2 below; this notification depends on FR-12's
debounced update signal, which isn't built yet.

## FR-12 — Session profile change detection

**UT-12.1 — Tray icon "updated" flash on archive change (FR-12.1/
FR-12.3)** ✅

Covered above by UT-T.3 — the "updated" icon flash is this requirement's
only user-visible effect today.

**UT-12.2 — Debounced profile-update notification (FR-12.2, gates
FR-11.4)** ⏳ NOT IMPLEMENTED YET

No test steps — the debounce/grace period (`MUTAGEN_PROFILE_GRACE`) and
the desktop notification it would gate are not built yet; only the raw,
undebounced signal behind UT-12.1 exists today.

## FR-13 — Automatic session recovery ⏳ NOT IMPLEMENTED YET

No test steps — see
[05-wpf-migration-notes.md §6, Phase 5](05-wpf-migration-notes.md#6-suggested-phased-delivery).
As already observed in UT-T.8/UT-7.2: today, a stopped or missing session
is only revived by "Reload config & restart mutagen" or a manual app
restart — never automatically.

## FR-14 — Logging & diagnostics

**UT-14.1 — An unhandled exception is logged and shown to the user
(FR-14.1)** ✅

* Left-click the tray icon to open the status view.
* Click the "Boum" button (a deliberate test button included specifically
  to re-verify this path without needing a real crash).
* A blocking window is displayed, titled "MutagenMon — error", with the
  content "MutagenMon hit an unexpected error and will close:" followed
  by exception details.
* Click "OK".
* The application closes.
* `log/mutagenMon.log` contains a Critical-level entry with the same
  exception.

**UT-14.2 — A startup failure is logged and shown (FR-14.1)** ✅

* Stop MutagenMon.
* Rename `config/config_mutagenmon.json` (or edit it so the JSON is
  invalid) so config loading fails.
* Start MutagenMon.
* A window is displayed, titled "MutagenMon — startup error", with the
  content "MutagenMon failed to start:" followed by exception details.
* `log/mutagenMon.log` contains a matching Critical-level entry.
* Restore the config file afterwards.

**UT-14.3 — Resolution log is a separate file from the main log
(FR-14.3, partial)** ✅

Covered above by UT-9.3/UT-9.4/UT-10.1 — `resolve.log` is independent of
`mutagenMon.log`.

**UT-14.4 — Dedicated restart log (FR-14.3, restart half)** ⏳ NOT
IMPLEMENTED YET

No dedicated `restart.log` exists yet. The one self-restart mechanism
that is implemented today (staleness watchdog, FR-6 — see UT-T.11) logs
to the same single `mutagenMon.log` as everything else.

**UT-14.5 — Verbosity gate (FR-14.2)** ⏳ NOT IMPLEMENTED *(deliberate — see
[05-wpf-migration-notes.md §7](05-wpf-migration-notes.md#7-logging))*

No test steps — `DEBUG_LEVEL` has no effect in the rewrite by design;
every log level is always written.

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
* FR-11.1/FR-11.2 (new-conflict and auto-resolve notifications) are
  implemented. FR-11.3 (stuck-connection-restart notification, moved to
  Phase 5 with FR-13), FR-11.4 (profile-update notification, gated by
  FR-12.2), FR-12.2 (debounced profile-update signal), FR-13 (automatic
  session recovery), FR-14.2 (verbosity gate), and the restart half of
  FR-14.3 are not implemented — see the ⏳ sections above and
  [05-wpf-migration-notes.md §6](05-wpf-migration-notes.md#6-suggested-phased-delivery)
  for the plan.
