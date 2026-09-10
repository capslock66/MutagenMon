# Session Management (Add / Edit / Delete) — Requirements & Design

This document specifies a new capability: creating, editing and deleting
synchronization sessions from the status view (FR-8), instead of only by
hand-editing `mutagen-create.bat` (FR-1.1) and restarting the application
or issuing "Reload config & restart" (FR-7.1).

**Status: implemented.** Everything below is built: the Core model/parser
(`MutagenMon.Core/Sessions/SessionCommandLine.cs`,
`SessionCommandLineParser.cs`, `SessionCommandLineEnumFormatting.cs`), the
file mutation (`SessionFileMutator.cs`), the live orchestration
(`SessionEditingService.cs`), and the App-layer UI (`SessionEditWindow.xaml`,
the status view's toolbar and grid column in `StatusWindow.xaml`). Manually
verified on Windows (2026-09-10).

Decisions below marked **(confirmed)** were settled with the user during
the design discussion that produced this document.

## Relationship to existing requirements

- Extends [01-functional-requirements.md](01-functional-requirements.md)
  FR-1.1 (session source of truth is `mutagen-create.bat`), FR-1.2 (name
  uniqueness), FR-7.1 (reload/recreate), FR-8 (status view grid), and
  FR-13.5 (terminate + recreate mechanics, which the Edit/Delete actions
  below reuse).
- Adds new functional requirements FR-16 through FR-27, continuing the
  numbering from `01-functional-requirements.md`'s FR-15. They are kept in
  this separate file rather than appended to `01-functional-requirements.md`
  because of their size and because they describe a not-yet-built feature
  rather than the current application.

## FR-16 — Status view toolbar

- FR-16.1: The status view (FR-8) MUST gain a toolbar above the sessions
  grid, containing:
  - An **"Add"** action (icon + label or icon with tooltip) that opens the
    add/edit window (FR-17) in "create" mode. Icon: `PackIconMaterial
    Kind="PlusThick"` (see FR-16.3 for the icon package).
  - The **"Reload config & restart mutagen"** action (FR-7.1), relocated
    here from the bottom button row **(confirmed)** — it no longer appears
    in the bottom `Grid.Row="2"` action row of `StatusWindow.xaml`.
- FR-16.2: The relocated reload action MUST keep its existing behavior and
  disabled-while-reloading state (FR-7.5, FR-8.5) unchanged — only its
  position and icon change here.
- FR-16.3: The reload action's icon MUST NOT be a generic circular
  "refresh" glyph **(confirmed)** — it needs to read as "reload
  configuration", not "refresh this view". Icon: `PackIconMaterial
  Kind="CogRefreshOutline"` **(confirmed)**. NuGet package for this and
  every other icon in this feature (Add/Edit/Delete, FR-17.1):
  **`MahApps.Metro.IconPacks.Material`** (installed: 6.2.1) — usable in
  plain WPF with no dependency on the MahApps.Metro control library,
  chosen over adding custom `.ico`/`.png` bitmap assets because it's
  vector (crisp at any toolbar/grid-cell size) and MIT/MS-PL licensed.
  The four `PackIconMaterialKind` names this feature uses
  (`PlusThick`/`PencilOutline`/`TrashCanOutline`/`CogRefreshOutline`)
  were confirmed to exist in that installed version before relying on
  them.
- FR-16.4: "Edit" and "Delete" are NOT toolbar actions — they exist only
  as the per-row icons described in FR-17 below **(confirmed: row icons
  only, no toolbar duplication)**.

## FR-17 — Grid row actions (Edit / Delete)

- FR-17.1: The sessions grid (FR-8.1) MUST gain a new leftmost column,
  before "Name", containing two small icon buttons per row: **Edit**
  (`PackIconMaterial Kind="PencilOutline"`) and **Delete**
  (`PackIconMaterial Kind="TrashCanOutline"`) — see FR-16.3 for the icon
  package.
- FR-17.2: Edit opens the add/edit window (FR-18) in "edit" mode,
  pre-populated from that row's session (FR-18, FR-26).
- FR-17.3: **(confirmed)** Delete MUST show a blocking confirmation
  dialog before doing anything: title `"Mutagen delete session"`, body
  `"Delete session <name> ?"`, two buttons **Yes** and **No** (both
  simply close the dialog). Declining (**No**) leaves everything
  unchanged. The dialog does not need to spell out that this also
  terminates the running session (FR-17.4) — implied by "delete".
- FR-17.4: Confirmed delete MUST, immediately **(confirmed: immediate
  apply)**:
  1. Request termination of the running session
     (`mutagen sync terminate <name>`), tolerating failure the same way
     FR-13.5 does (already-gone session must not block the rest of the
     deletion).
  2. Remove that session's line entirely from `mutagen-create.bat` (not
     just comment it out with `rem ` — leaving it as `rem `-prefixed would
     make it inert but still visible/confusing, and provides no benefit
     over just deleting it since the source of truth is the file itself,
     which is presumably under the user's own version control if history
     is wanted).
  3. Remove the session from the live in-memory session list so the grid
     updates on the next render without waiting for a full reload.
- FR-17.5: If step 1 or 2 of FR-17.4 fails unexpectedly (e.g. the file is
  read-only, or a race with an external edit), the application MUST show
  an error dialog and MUST NOT silently drop the session from the grid if
  the file removal did not actually succeed (grid state must stay
  consistent with the file, which remains the source of truth per FR-1.1).

## FR-18 — Add/Edit window: identity fields

- FR-18.1: The window (title `"MutagenMon: Add session"` or
  `"MutagenMon: Edit session <name>"`) has three labeled single-line edit
  boxes at the top, in this order: **Name**, **Alpha**, **Beta**.
- FR-18.2: Name MUST be validated as: non-empty, containing no whitespace
  (consistent with FR-1.1's `--name=(\S+)` extraction — a name with a
  space would silently break re-parsing after save), and unique among all
  other currently-configured sessions (FR-1.2) — case-sensitive, matching
  `SessionDefinitionLoader`'s ordinal dictionary lookup. In edit mode, the
  session's own current name is excluded from the uniqueness check.
- FR-18.3: Alpha and Beta are the two synchronization endpoint
  paths/URLs, used as-is as the two positional arguments of
  `mutagen sync create <alpha> <beta> ...`. No format validation beyond
  non-empty is proposed here — mutagen itself already rejects malformed
  endpoint URLs when the command actually runs (FR-27).
- FR-18.4: Save is disabled until Name/Alpha/Beta are all non-empty and
  Name passes FR-18.2.

## FR-19 — Sync mode

- FR-19.1: A group box **"Options"** contains, first, a **"Sync mode"**
  combobox with exactly these four values (mutagen's own sync mode
  values): `two-way-safe` (default/pre-selected), `two-way-resolved`,
  `one-way-safe`, `one-way-replica`.
- FR-19.2: **(confirmed)** Parsing recognizes `--mode`, `-m`, and
  `--sync-mode` as synonyms — all three set the same `Mode` field.
  `mutagen sync create --help` (checked 2026-09-09) confirms `-m,
  --mode` as the flag pair; the synchronization docs page separately
  lists `-m`/`--sync-mode`, suggesting `--sync-mode` is an older/alias
  name mutagen still accepts (still not independently confirmed against
  a live `mutagen` binary, but treated as equivalent per this
  **(confirmed)** decision rather than left as an unknown flag). This
  also fixes what manual testing found in the repo's own sample
  `mutagen-create.bat`, which uses `--sync-mode=two-way-resolved` — that
  now parses as `Mode = TwoWayResolved` instead of falling into "Unknown
  flags".
  Rendering, regardless of which synonym was in the source line, always
  emits the canonical `-m <value>` (never `--mode=`/`--sync-mode=`) —
  saving a session normalizes its mode flag to `-m`. Omitted entirely when
  the selected value is the default `two-way-safe`, to keep generated
  lines close to what a human would hand-write (mutagen behaves
  identically either way since that's already its own default). This is
  the general rule for every default-valued combobox in this window
  (FR-22 symlink mode, FR-23 watch mode, FR-24 probe/scan mode, FR-25
  stage mode): a flag is only ever emitted when the selected value
  differs from mutagen's own default for it.

## FR-20 — Ignore patterns

- FR-20.1: A multi-line text box **"Ignore"** inside the Options group
  box, one pattern per line.
- FR-20.2: Each non-empty line MUST map to its own repeated flag:
  `-i <patternN>` (one `-i` per line, not the comma-separated
  `--ignore=a,b` alternative form — repeated flags are simpler to
  round-trip line-by-line and avoid ambiguity if a pattern itself
  contains a comma).
- FR-20.3: Patterns are otherwise passed through verbatim (including a
  leading `!` for negation, per mutagen's git-like ignore syntax) — no
  syntax validation is proposed.
- FR-20.4: A pattern containing whitespace MUST be quoted in the
  generated batch-file line (`-i "pattern with spaces"`), consistent with
  Windows batch argument quoting.
- FR-20.5: **(confirmed)** A **"Ignore VCS directories"** checkbox,
  independent of the pattern list above, maps to
  `--ignore-vcs`/`--no-ignore-vcs`. It is **tri-state**: indeterminate
  (default) omits both flags (defers to whatever `~/.mutagen.yml` says
  globally), checked emits `--ignore-vcs`, unchecked emits
  `--no-ignore-vcs` — an explicit unchecked state is meaningfully
  different from "unset" here, unlike every other blank-means-omitted
  field in this window, because `--no-ignore-vcs` is itself a flag that
  overrides a possible global default rather than mutagen's built-in
  default.
- FR-20.6: **(confirmed)** A **"Use Docker ignore syntax"** checkbox,
  also independent of the pattern list, maps to `--ignore-syntax`:
  unchecked (default) omits the flag (native `mutagen` syntax), checked
  emits `--ignore-syntax=docker`.

## FR-21 — Permissions

- FR-21.1: **(confirmed)** A standalone **"Permissions mode"** combobox
  at the top of the zone — `portable` (default) / `manual` — with no
  link to the Owner/Group/File/Directory-mode rows below (no gating,
  greying-out, or validation between them; each is just its own
  independent flag). Maps to `--permissions-mode=<value>`, omitted when
  `portable` (FR-19.2's general default-omission rule).

- FR-21.2: A **"Permissions"** zone inside the Options group box, laid
  out as a 3-column grid — **Session** / **Alpha** / **Beta** — with one
  row per setting **(confirmed scope: owner + group + file/directory
  modes)**:
  | Row | Session flag | Alpha flag | Beta flag |
  |---|---|---|---|
  | Owner | `--default-owner=<owner>` | `--default-owner-alpha=<owner>` | `--default-owner-beta=<owner>` |
  | Group | `--default-group=<group>` | `--default-group-alpha=<group>` | `--default-group-beta=<group>` |
  | File mode | `--default-file-mode=<mode>` | `--default-file-mode-alpha=<mode>` | `--default-file-mode-beta=<mode>` |
  | Directory mode | `--default-directory-mode=<mode>` | `--default-directory-mode-alpha=<mode>` | `--default-directory-mode-beta=<mode>` |
- FR-21.3: Every cell is a plain edit box, blank by default; a blank cell
  means the corresponding flag is omitted entirely.
- FR-21.4: Owner/Group accept `id:<N>` (POSIX), `sid:<...>` (Windows), or
  a literal name. File/Directory mode accept octal (`0644`, `750`, ...);
  no client-side validation is proposed beyond non-empty — mutagen
  rejects malformed values itself.
- FR-21.5: **(confirmed)** Setting both a Session-column value and an
  Alpha/Beta value on the same row is allowed — the UI does not prevent
  it or enforce "one or the other". `mutagen sync create --help` lists
  each of these (e.g. `--default-owner` / `--default-owner-alpha` /
  `--default-owner-beta`) as three independent, ordinarily-parsed flags
  with no documented mutual-exclusion, consistent with the per-side flag
  simply overriding the session-wide default for that side only. This
  same reasoning applies identically to FR-23 (Watching), FR-24
  (Probing and scanning) and FR-25 (Staging), which share the same
  3-column layout — see FR-23.3, FR-24.3, FR-25.3.

## FR-22 — Symbolic links

- FR-22.1: A **"Symbolic links"** zone with a single combobox: `ignore`,
  `portable` (default), `posix-raw`. Maps to `--symlink-mode=<value>`,
  omitted when `portable` (FR-19.2's general default-omission rule). No
  per-side variant exists for this flag.

## FR-23 — Watching

- FR-23.1: A **"Watching"** zone using the same Session/Alpha/Beta
  3-column layout as Permissions (FR-21):
  | Row | Session flag | Alpha flag | Beta flag |
  |---|---|---|---|
  | Watch mode (combobox: `portable` default / `force-poll` / `no-watch`) | `--watch-mode=<value>` | `--watch-mode-alpha=<value>` | `--watch-mode-beta=<value>` |
  | Polling interval, seconds (numeric, default blank = mutagen's own 10s default) | `--watch-polling-interval=<n>` | `--watch-polling-interval-alpha=<n>` | `--watch-polling-interval-beta=<n>` |
- FR-23.2: Watch mode follows FR-19.2's general default-omission rule
  (flag omitted when the cell is left at/set to `portable`); polling
  interval is omitted whenever its cell is blank.
- FR-23.3: **(confirmed)** Combining Session and per-side values on the
  same row is allowed — resolved the same way as FR-21.5.

## FR-24 — Probing and scanning

- FR-24.1: A **"Probing and scanning"** zone, same 3-column layout:
  | Row | Session flag | Alpha flag | Beta flag |
  |---|---|---|---|
  | Probe mode (combobox: `probe` default / `assume`) | `--probe-mode=<value>` | `--probe-mode-alpha=<value>` | `--probe-mode-beta=<value>` |
  | Scan mode (combobox: `accelerated` default / `full`) | `--scan-mode=<value>` | `--scan-mode-alpha=<value>` | `--scan-mode-beta=<value>` |
- FR-24.2: Both rows follow FR-19.2's general default-omission rule
  (flag omitted when the cell is left at/set to its default value shown
  above).
- FR-24.3: **(confirmed)** Combining Session and per-side values on the
  same row is allowed — resolved the same way as FR-21.5.

## FR-25 — Staging

- FR-25.1: A **"Staging"** zone, same 3-column layout, single row:
  | Row | Session flag | Alpha flag | Beta flag |
  |---|---|---|---|
  | Stage mode (combobox: `mutagen` default / `neighboring`) | `--stage-mode=<value>` | `--stage-mode-alpha=<value>` | `--stage-mode-beta=<value>` |
- FR-25.2: Follows FR-19.2's general default-omission rule (flag omitted
  when the cell is left at/set to `mutagen`).
- FR-25.3: **(confirmed)** Combining Session and per-side values on the
  same row is allowed — resolved the same way as FR-21.5.

  > **Correction**: an earlier draft of this document listed a third
  > `internal` stage mode value, sourced from a documentation page
  > summary. The actual `mutagen sync create --help` output (checked
  > 2026-09-09) only lists `mutagen|neighboring` for `--stage-mode`
  > (and its `-alpha`/`-beta` variants) — `--help` is the ground truth
  > used here, so `internal` has been removed.

## FR-26 — Limits

- FR-26.1: A **"Limit"** zone with two plain fields (session-wide only —
  mutagen documents no per-side variant for either):
  - **Max staging file size**: free-text, human-friendly size (e.g.
    `1000 MB`) or a plain byte count. Maps to
    `--max-staging-file-size=<value>`, omitted when blank (mutagen's own
    default of unlimited).
  - **Max entry count**: numeric. Maps to `--max-entry-count=<value>`,
    omitted when blank.

## FR-27 — Command-line generation, parsing, and round-trip fidelity

- FR-27.1: **Edit mode parsing**: opening Edit on a row MUST parse that
  session's stored `mutagen-create.bat` line, extracting every flag
  recognized by FR-18 through FR-26 into its corresponding control.
- FR-27.2: **(confirmed)** Recognition works against a maintained
  dictionary of every flag documented in FR-19 through FR-26 (e.g.
  `-m`, `--default-owner`, `--default-owner-alpha`, ...), one
  entry per flag name, each knowing whether it takes a value (all of them
  do except `--ignore-vcs`/`--no-ignore-vcs`, which are boolean
  presence-only flags — every other recognized flag here takes exactly
  one value, either
  as `--flag=value` or as two separate tokens `--flag value`). Walking
  the line's tokens (after the positional alpha/beta and `--name=`),
  each token is looked up in the dictionary:
  - A match consumes that flag (and its value token, if not in `=` form)
    into the corresponding control (FR-19–FR-26).
  - A non-match starts a new **unknown flag unit**: the token itself,
    plus the following token as its value *unless* that following token
    itself looks like a flag (starts with `-`/`--`) or is the last token
    on the line — a heuristic, since an unrecognized flag's arity (does
    it take a value at all?) isn't known to the app.
- FR-27.3: **(confirmed)** Every unknown flag unit is shown in a
  dedicated **"Unknown flags"** zone (its own group box, separate from
  "Options"), with the group box **title rendered in red**. The zone is
  hidden/collapsed entirely when there are no unknown flag units (nothing
  to show, nothing to warn about). Inside it, one row per unknown flag
  unit: a **checkbox, checked by default**, followed by that unit's raw
  text (e.g. `☑ --foo-bar=baz`). Unchecking a row means "drop this on
  Save" — it will NOT be re-emitted into the regenerated line (FR-27.4);
  leaving it checked (the default) preserves it verbatim, so nothing is
  silently lost unless the user explicitly opts out.
- FR-27.4: **Save (both Add and Edit)** MUST, immediately **(confirmed:
  immediate apply, same as Delete/FR-17.4)**:
  1. Build the full command line in a fixed canonical order: `mutagen
     sync create <alpha> <beta> --name=<name>` followed by sync mode
     (FR-19), ignores (FR-20), permissions (FR-21), symlink mode (FR-22),
     watching (FR-23), probing/scanning (FR-24), staging (FR-25), limits
     (FR-26), then any still-**checked** unknown flag units (FR-27.3)
     appended last, in their original relative order; unchecked units are
     dropped.
  2. On Add: run the built `mutagen sync create ...` command to actually
     create the session, then append the line to `mutagen-create.bat`.
  3. On Edit: terminate the existing running session
     (`mutagen sync terminate <old-name>`, tolerating failure per
     FR-13.5), run the newly built `sync create` command, then replace
     that session's line in `mutagen-create.bat` **in place** (same file
     position) rather than moving it to the end.
  4. If the `mutagen` CLI call itself fails (invalid flag combination,
     unreachable endpoint, etc.), the window MUST show the CLI's error
     output and MUST NOT touch `mutagen-create.bat` — the file is only
     updated after the live command has succeeded, so the file and the
     running session-set never diverge. **The window itself MUST stay
     open** in this case, with every field exactly as the user left it,
     and MUST re-enable Save/Cancel for another attempt — closing first
     and reporting the failure afterwards would silently discard
     everything the user typed. (An earlier implementation got this
     wrong — the window closed immediately on Save, before the async CLI
     call even ran — confirmed by manual testing on both Add, which
     forced retyping the whole form, and Edit, whose just-added field
     came back blank on the next Edit; fixed by having Save raise an
     event instead of closing the window directly, with the caller only
     closing it once the CLI call has actually succeeded.)
- FR-27.5: After a successful Add/Edit, the status view's in-memory
  session list and grid MUST update immediately, without waiting for the
  next poll cycle or a manual reload.
  - **Current implementation note**: `SessionMonitorService` has no API to
    add, edit, or remove a single session from its already-running poll
    loop — it was built assuming a fixed session list, refreshed only by
    the full FR-7.1 "Reload config & restart" reload. The current
    implementation reuses that existing reload pathway after a successful
    Add/Edit/Delete to bring the change into the live grid, rather than
    patching the in-memory list directly. This is heavier than "immediately,
    without... a manual reload" as written above — it stops and restarts
    *every* configured session, not just the one that changed. A lighter
    incremental-update path on `SessionMonitorService` would be a real,
    separate piece of Core work, not yet done.

## Design: Add/Edit window layout

```
┌─ MutagenMon: Add session ──────────────────────────────────────────┐
│  Name       [________________________________]                    │
│  Alpha      [________________________________]                    │
│  Beta       [________________________________]                    │
│                                                                     │
│ ┌─ Options ────────────────────────────────────────────────────┐  │
│ │ Sync mode   [two-way-safe        ▾]                          │  │
│ │                                                               │  │
│ │ Ignore                                                       │  │
│ │ ┌───────────────────────────────────────────────────────┐   │  │
│ │ │ node_modules/                                          │   │  │
│ │ │ *.tmp                                                   │   │  │
│ │ │                                                          │   │  │
│ │ └───────────────────────────────────────────────────────┘   │  │
│ │ ☐ Ignore VCS directories (tri-state)  ☐ Use Docker ignore syntax │
│ │                                                               │  │
│ │ Permissions mode  [portable               ▾]                │  │
│ │ Permissions          Session        Alpha          Beta      │  │
│ │   Owner              [______]       [______]       [______]  │  │
│ │   Group              [______]       [______]       [______]  │  │
│ │   File mode           [______]       [______]       [______]  │  │
│ │   Directory mode       [______]       [______]       [______]  │  │
│ │                                                               │  │
│ │ Symbolic links      [portable            ▾]                  │  │
│ │                                                               │  │
│ │ Watching             Session        Alpha          Beta      │  │
│ │   Watch mode         [portable  ▾]  [       ▾]  [       ▾]   │  │
│ │   Polling interval(s) [______]       [______]       [______]  │  │
│ │                                                               │  │
│ │ Probing and scanning Session        Alpha          Beta      │  │
│ │   Probe mode          [probe    ▾]  [       ▾]  [       ▾]   │  │
│ │   Scan mode           [accelerated▾] [      ▾]  [       ▾]   │  │
│ │                                                               │  │
│ │ Staging               Session        Alpha          Beta      │  │
│ │   Stage mode          [mutagen  ▾]  [       ▾]  [       ▾]   │  │
│ │                                                               │  │
│ │ Limit                                                        │  │
│ │   Max staging file size [__________]                        │  │
│ │   Max entry count       [__________]                         │  │
│ └───────────────────────────────────────────────────────────────┘  │
│                                                                     │
│ ┌─ Unknown flags ── (title in red) ───────────────────────────┐   │  ← whole zone hidden if empty
│ │ ☑ --foo-bar=baz                                              │   │
│ │ ☑ --some-future-flag=value                                   │   │
│ └───────────────────────────────────────────────────────────────┘   │
│                                                                     │
│                                            [ Cancel ]  [ Save ]    │
└─────────────────────────────────────────────────────────────────────┘
```

Notes:
- Given the number of zones, the Options group box is expected to need a
  scrollable content area (a `ScrollViewer` around the group box's inner
  `StackPanel`) so the window stays a reasonable fixed height rather than
  growing to fit eight zones — matching `StatusWindow`'s existing
  `ResizeMode="CanResize"` pattern.
- Each 3-column zone (Permissions/Watching/Probing&scanning/Staging) is
  plain, independently hand-written XAML (a `Grid` per zone) rather than
  a shared control template — reconsidered during implementation:
  repeating markup across four near-identical zones is normal/acceptable
  for declarative UI, and a generic templated control would have been
  more machinery than four small Grids warranted.

## Status view toolbar sketch

```
┌─ MutagenMon ─────────────────────────────────────────────────────────┐
│ [+ Add]                                            [⟲ Reload config] │
├───┬──────┬─────────────────────┬──────────┬──────────┬──────────────┤
│   │ Name │ Status              │ Alpha    │ Beta     │ Last changed │
├───┼──────┼─────────────────────┼──────────┼──────────┼──────────────┤
│✎🗑│ web  │ ● Watching for chg. │ /a/path  │ /b/path  │ 2h ago       │
└───┴──────┴─────────────────────┴──────────┴──────────┴──────────────┘
```

`✎`/`🗑` stand in for the actual Edit/Delete icons
(`PencilOutline`/`TrashCanOutline`); `⟲` stands in for the reload icon
(`CogRefreshOutline`), explicitly **not** a plain circular refresh glyph
per FR-16.3.

## Implementation

Built across `MutagenMon.Core/Sessions/` (`SessionCommandLine.cs`,
`SessionCommandLineParser.cs`, `SessionCommandLineEnumFormatting.cs`,
`SessionFileMutator.cs`, `SessionEditingService.cs`) and
`MutagenMon.App/` (`SessionEditWindow.xaml`/`.xaml.cs`,
`StatusWindow.xaml`/`.xaml.cs`, wired up in `App.xaml.cs`). Unit-tested in
`MutagenMon.Core.Tests` (parser round-trip, file mutation, editing-service
orchestration including the "must not touch the file on failure" cases).
Manually verified on Windows (2026-09-10).
