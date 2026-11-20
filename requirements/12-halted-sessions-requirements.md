# Halted Sessions — Requirements & Design

This document specifies how MutagenMon handles a session that Mutagen has
**halted** for safety (status text starting with `Halted`), and in
particular how it resolves the most common case, **"Halted due to
one-sided root emptying"**, manually or automatically.

**Status: implemented** (phases 3 and 4 of the plan
`.planning/2026-11-17-auto-resolve-root-emptying-halt/`: recognition,
configuration and directive, then the recovery, the manual action, the
automatic fix and the notifications). The manual tests (UT-57.x, UT-6x.x in
[UserTests.md](UserTests.md)) are written and still to be run. Continues the FR numbering from
[11-scripts-tab-requirements.md](11-scripts-tab-requirements.md)'s FR-56,
as FR-57 through FR-65.

## Background

Mutagen has a safety check: when one synchronization root (and not both)
loses a non-trivial amount of content and is left empty, the session
**halts** instead of propagating the deletion. A halted session stays
parked until the user restarts it, resets it or recreates it. A typical
trigger is a developer who clears a synchronised folder and refills it
immediately: a scan that lands on the empty instant halts the session.
This is expected behaviour, not a malfunction, and it should not need
manual work.

Facts established by tests on a real session (2026-11-18) and by reading
Mutagen's source (`pkg/synchronization/controller.go`, master branch; read,
not executed here):

- While the session is halted, `mutagen sync list <name>` returns the same
  output whatever is done on either side: it is the snapshot taken when
  the halt occurred. The "Synchronizable contents" of each endpoint show
  which side was emptied (see FR-58).
- `mutagen sync reset <name>` clears the session history (the *ancestor*)
  and restarts the session by itself; a separate `resume` is not needed.
  Reset **alone** is not a solution: with an empty ancestor, the content
  that still exists on the non-emptied side is seen as new and is copied
  back to the emptied side (confirmed by a test).
- The documented manual recovery is to delete the content that would be
  deleted, then resume. Purging the non-emptied side and then **resetting**
  (not resuming) lets the emptied side's current content flow to the purged
  side.
- Deleting a single file does not halt a session (the check targets "a
  non-trivial amount of content"); 4 files were enough in the test.

## Terminology

- **Emptied side (E)**: the endpoint whose root content was deleted.
- **Purged side (P)**: the other endpoint, whose content is deleted by the
  recovery. Either `alpha` or `beta` can be E; either can be a local
  folder or an SSH endpoint.
- **Root-emptying halt**: a halt whose status text matches the configured
  text `HaltedOneSidedRootEmptying` (FR-63).

## FR-57 — Recognition and display of halted sessions

- FR-57.1: Any session whose status text starts with `Halted`
  (case-insensitive) MUST be classified as code `50` (Problems, FR-3),
  regardless of the code it had before. This covers the three Mutagen
  halts: root emptying, root deletion and root type change. The exact
  wording of the last two is not verified and MUST NOT be relied on: only
  the `Halted` prefix is.
- FR-57.2: The aggregated status (FR-4) then drives the tray icon through
  the existing `Problems` row of
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §3
  (`green-error`, tooltip "problems"). No new icon and no new tooltip are
  introduced. A session that is already halted when the application starts
  MUST be classified on the first poll, not left at code `0`.
- FR-57.3: In the status grid (FR-8.1), the row of a halted session MUST
  be drawn with a **red** background (`#C62828`, white text — legible in
  light and dark themes) and MUST return to normal on the first poll that no longer
  reports a `Halted` status. The Status column keeps showing the raw status
  text.
- FR-57.4: A halted session MUST NOT be terminated or restarted by the
  automatic recovery of FR-13: none of its three causes applies to a halted
  session, and the classification of FR-57.1 MUST NOT create a fourth one.
- FR-57.5: The detection text for the automatic and manual resolution of
  the root-emptying halt is the configuration key
  `HaltedOneSidedRootEmptying` (FR-63), compared case-insensitively
  (`OrdinalIgnoreCase`) at the start of the status text. The other two
  halts are only displayed (FR-57.1 to FR-57.3) and notified (FR-62): they
  are never resolved by MutagenMon.

## FR-58 — Identifying the emptied side

- FR-58.1: The `mutagen sync list` parser (FR-2.2) MUST also read, for each
  endpoint, the "Synchronizable contents" block: the number of
  directories, files and symbolic links (the sizes are ignored). Matching
  is tolerant: a number followed by a keyword starting with `director`,
  `file` or `symbolic link`, so singular and plural wordings both work.
  Real example (halted session `temp`, alpha emptied):

  ```
  Alpha:
          URL: C:\sources\temp
          Connected: Yes
          Synchronizable contents:
                  1 directory
                  0 files (0 B)
                  0 symbolic links
  Beta:
          URL: robbie:sources/temp
          Connected: Yes
          Synchronizable contents:
                  1 directory
                  4 files (0 B)
                  0 symbolic links
  Status: Halted due to one-sided root emptying
  ```

- FR-58.2: An endpoint is **empty** when it has no file, no symbolic link
  and at most one directory (the root itself). E is determined when exactly
  one endpoint is empty and the other is not. If both endpoints are empty,
  neither is, or the block is missing, E is **undetermined**: nothing is
  purged, the situation is logged, and the halt is only notified (FR-62).
- FR-58.3: The counts MUST be taken from the poll that reports the halt
  (they are a frozen snapshot while the session is halted) and MUST NOT
  trigger any live inspection of the endpoints. The file count of P
  displayed by the manual action (FR-60) is P's count from this snapshot.

## FR-59 — Recovery sequence (shared by FR-60 and FR-61)

Given a root-emptying halt with E determined, the recovery MUST run these
steps, in this order, stopping at the first failure:

1. **Resolve P's root.** Local endpoint: the folder path. SSH endpoint
   (`[user@]host:path`, host handled like the other SSH features, FR-9.1
   and the `SshPath` key): resolve the absolute path on the server
   (`cd -P`, then `pwd -P`) — a relative path is relative to the SSH home.
2. **Path guard.** Refuse an empty path and the filesystem root: `/` on a
   POSIX server, a drive root such as `C:\` for a local Windows folder.
   Nothing else is refused: a one-level folder such as `/media` or a whole
   `/home` is a legitimate synchronisation root (a mirror of a home area is
   a supported use).
3. **Purge P's content.** Delete everything *inside* P's root, never the
   root itself.
   - SSH (POSIX servers only): `find <root> -mindepth 1 -xdev -delete`,
     with the path quoted by `ShellQuoting`. ssh is run with a forced
     terminal (`-tt`), so that killing the local ssh process (timeout,
     cancellation) hangs the remote `find` up instead of leaving it deleting
     in the background.
   - Local folder: delete every child, entry by entry, on a background
     thread (never the UI thread); symbolic links and junctions are removed
     as links and never traversed, and a root that is itself a link is
     resolved first, so the root guard applies to the real folder.
   - The step is bounded by a timeout (`ScriptTimeoutSeconds`, FR-51.4; a
     value of 0 or less means no timeout). On timeout or cancellation the
     deletion stops (ssh process killed, local loop interrupted).
4. **Reset.** Run `mutagen sync reset <name>` immediately. The purge MUST
   come before the reset, because the reset restarts the synchronisation at
   once. No `resume` is issued, and there is no waiting and no inspection of
   E's current content: E's content, as it is at that moment, becomes the
   source and later changes propagate normally.
5. **Check.** The recovery is successful when a later poll no longer
   reports the halt (FR-61.4 defines what happens otherwise).

- FR-59.1: Endpoints that are not a local folder or a POSIX SSH server
  (Windows servers reached over SSH, which have no `find`; transports other
  than SSH, such as Docker) are **not supported**: the recovery refuses to
  run, with a message naming the reason (FR-60.4, FR-62).
- FR-59.2: Only one recovery MAY run for a given session at a time. The
  polling loop MUST NOT be blocked while it runs.
- FR-59.3: Each step MUST be logged (session, E, P, root, outcome; the
  command output when a step fails). The destructive step logs the number
  of files announced by the snapshot (FR-58.3).
- FR-59.4: The recovery makes E authoritative: **P's content is destroyed,
  including files that Mutagen ignores and files not yet synchronised.**

## FR-60 — Manual action "Accept deletion"

- FR-60.1: The sessions grid's actions column (FR-17.1) MUST show an extra
  icon button, **only on rows halted by a root-emptying halt** (FR-57.5).
  Icon: a `PackIconMaterial` kind consistent with FR-16.3 (e.g.
  `DeleteSweepOutline`, to be checked against the installed icon pack at
  implementation time); tooltip "Accept deletion".
- FR-60.2: Clicking it MUST show a blocking confirmation dialog, title
  `"Mutagen accept deletion"`, naming the session, the emptied side (URL),
  the purged side (URL) and the number of files that will be deleted (FR-58.3),
  with **Yes** and **No** (**No** changes nothing).
- FR-60.3: **Yes** runs the recovery of FR-59 immediately. The action does
  not depend on the auto-fix flags, on the cooldown or on the sync mode:
  it is an explicit user decision. Because the dialog can stay open for a
  long time, **Yes** first re-reads the latest poll and refuses (no deletion,
  a warning dialog gives the reason) when the status is more than 30 seconds
  old, when its listing began before the end of the last recovery of this
  session (see FR-61.1), when the session no longer shows a recoverable
  root-emptying halt, or
  when the emptied side or the side to purge is no longer the one that was
  shown: a session that synchronises again would propagate the deletions.
- FR-60.4: If E is undetermined (FR-58.2) or P is not supported (FR-59.1),
  the dialog MUST NOT be offered; an error dialog states the reason
  instead. A failure during the recovery shows an error dialog with the
  failing step and the command output. A success shows nothing: the row
  returns to normal on a later poll.

## FR-61 — Automatic resolution

- FR-61.1: The recovery of FR-59 runs automatically, on the poll that
  reports a root-emptying halt, when **all** of the following hold:
  1. the effective auto-fix flag is `true`: the session's directive
     (FR-64) if present, otherwise the global key
     `HaltedOneSidedRootEmptyingAutoFix` (FR-63, default `false`);
  2. monitoring is enabled (not paused by the user, FR-7), as for the
     automatic restarts of FR-13;
  3. the session's sync mode is `two-way-safe` (the session's create line,
     FR-18/FR-19; absent `-m` means `two-way-safe`);
  4. E is determined (FR-58.2) and P is supported (FR-59.1);
  5. the cooldown has elapsed (FR-61.3) and fewer than 3 consecutive
     attempts failed (FR-61.4);
  6. the status listing did not begin before the end of the last recovery of
     the session, whoever ran it (it could show the very halt that recovery
     just resolved).
- FR-61.2: There is no waiting before the run and no home-directory
  restriction: the only refused root is the filesystem root (FR-59, step 2).
- FR-61.3: **Cooldown.** Key `HaltedOneSidedRootEmptyingCooldownSeconds`
  (default `60`): minimum delay between the starts of two automatic runs on
  the same session. It also holds across halt episodes: a session that halts
  again right after a fix waits for the cooldown before the next run, so a
  purge + reset loop cannot start.
- FR-61.4: **Failures.** An automatic run *fails* if a step errors out or if
  the halt is still (or again) reported once the cooldown has elapsed, with
  no poll free of `Halted` in between. After **3 consecutive failures** no
  further automatic run is made for that halt (FR-62 notifies it); the
  counter resets as soon as a poll no longer reports a `Halted` status. A
  new halt after a successful run is a new run, subject to the cooldown.
- FR-61.5: The manual action (FR-60) remains available at any time.

## FR-62 — Notifications

Halt notifications are **not configurable**: there is no
`NotifyHaltedAutoFix` key (like FR-11.3b). Each is raised **once per halt
episode** — a session that reports `Halted…` and that stops reporting it
ends the episode — in the same way as the conflict notifications (FR-11.1).

- FR-62.1: **Halted, nothing automatic.** Raised when a session is halted
  and the recovery will not run on its own: the effective flag is `false`,
  or the conditions of FR-61.1 are not met (monitoring paused, mode other
  than `two-way-safe`, E undetermined, P not supported), or the halt is a
  root deletion or a root type change (never resolved by MutagenMon).
  Content: the session name and the status text.
- FR-62.2: **Automatic fix succeeded:** no notification.
- FR-62.3: **Automatic fix failed** (3 consecutive failures, FR-61.4):
  raised once, with the session name and the last error.
- FR-62.4: No notification is raised while a recovery is merely in progress
  or after a failed attempt that will be retried.

## FR-63 — Configuration keys

Three keys are added to
[06-configuration-reference.md](06-configuration-reference.md):

| Key | Type | Default |
|---|---|---|
| `HaltedOneSidedRootEmptying` | string | `"Halted due to one-sided root emptying"` |
| `HaltedOneSidedRootEmptyingAutoFix` | boolean | `false` |
| `HaltedOneSidedRootEmptyingCooldownSeconds` | integer (seconds) | `60` |

- FR-63.1: **Automatic addition.** At startup, after the file is loaded,
  every one of these three keys that is **absent** from
  `config_mutagenmon.json` MUST be added to the file with its default
  value. The addition edits the file as text (like `SaveWindowSize`, FR-8.6)
  so the `#` comments and every other key are kept; a key already present
  is never modified. If the file cannot be written, a warning is logged and
  startup continues: the in-memory default applies, with the same value.
- FR-63.2: In the "Mutagen monitor Configuration" tab
  ([09-mutagen-monitor-config-editor-requirements.md](09-mutagen-monitor-config-editor-requirements.md)):
  - a new **Halted sessions** section holds the three keys:
    `HaltedOneSidedRootEmptying` (text box, first), then
    `HaltedOneSidedRootEmptyingAutoFix` (checkbox) and
    `HaltedOneSidedRootEmptyingCooldownSeconds` (numeric text box);
  - each field has a tooltip from the table of 06; "Check" (FR-36.1) also
    requires the cooldown to be a whole number greater than or equal to 0
    and the status text not to be empty (an empty text would match the
    start of every status).
- FR-63.3: The global flag is only a default: a per-session directive
  (FR-64) overrides it in both directions.

## FR-64 — Per-session directive in `mutagen-create.bat`

- FR-64.1: **Syntax.** One line per session that overrides the global
  flag:

  ```
  rem sync --name=<session> : @halted-auto-fix: <true|false>
  ```

  The keyword and the value are case-insensitive, extra spaces around the
  `:` are tolerated, and the line MUST match as a whole (anything else is
  just a comment). A session with no directive inherits the global flag
  (FR-61.1).
- FR-64.2: **Compatibility.** The line starts with `rem`, so it is a valid
  batch comment and is skipped by the session loader like every `rem ` line
  (FR-1.1): it can never be taken for a session line, including by the
  Edit/Delete line lookup (FR-17.4, FR-27.4).
- FR-64.3: **Placement.** The directives are grouped in one block placed
  just above the blank line that precedes `mutagen sync terminate --all`,
  with exactly one blank line between the block and that line (the writer
  adds it if missing). If that line is absent, the block goes before the
  first session line, otherwise at the end of the file. The writer touches
  only directive lines.
- FR-64.4: **Reading.** The reader accepts a directive anywhere in the file
  (hand edits). If a session has several directives the last one wins and
  the writer collapses them to one. An invalid value, or a directive naming
  an unknown or disabled (`rem`-commented) session, is ignored with a log
  warning.
- FR-64.5: **Consistency.** Edit with a name change MUST rename the
  session's directive; Delete (FR-17.4) MUST remove it. A directive whose
  session line was deleted by hand stays in the file, is ignored and is
  logged.
- FR-64.6: The directive map is read together with the sessions (FR-1.1,
  FR-7.1 reload).

## FR-65 — Session editor field

- FR-65.1: The Add/Edit window (FR-18, FR-19.3) gains a **"Recovery"** tab
  in its left-hand section list, with one combobox, "Auto-fix of a halted
  session (root emptying)": **Inherit global setting**, **Enabled**,
  **Disabled**. Add defaults to *Inherit*; Duplicate starts from the value
  of the source session, like every other field it copies. Edit pre-selects
  the value read from the directive (FR-64.4). *Inherit* means no directive
  line.
- FR-65.2: Save writes the directive according to FR-64 (a line for
  *Enabled* and *Disabled*, none for *Inherit*) after the session itself
  was saved successfully; on failure the file is not touched (FR-27.4
  step 4).
- FR-65.3: **Amendment to FR-27.4.** When the only change of an Edit is the
  directive — the session keeps its name and the stored line and the
  regenerated `mutagen sync create` line give the same command once both go
  through the same parser and renderer, so a hand-written line that differs
  only by formatting counts as unchanged — Save MUST NOT terminate or
  recreate the session (which would reset its history and could cause the
  conflicts the recovery avoids): it rewrites the directive only (and does
  not touch the file at all when the directive is unchanged too), and the new
  effective flag MUST apply without restarting any session.
