# CLAUDE.md

Guidance for Claude Code (and any other agent) working in this repository.

## What this repository is

**MutagenMon** is a Wpf desktop utility that supervises [mutagen.io](https://github.com/mutagen-io/mutagen) file-synchronization sessions and reports their real-time status through a system tray icon. 
It can restart hung/errored sessions automatically and helps resolve sync conflicts 
(manually via a diff/merge tool, or automatically via configured path rules).

The repository contains:

```
mutagenMon/
├── MutagenMon.sln            # .NET solution (see README.md)
├── MutagenMon.App/           # WPF application project
├── MutagenMon.Core/          # Core logic (polling, status, config, resolution)
├── MutagenMon.Core.Tests/    # Unit tests
└── requirements/             # Behavioral specification: functional/non-functional
                               # requirements, tray icon state machine, config
                               # reference
```

- The .NET WPF application (`MutagenMon.sln` and the `MutagenMon.*`
  projects) lives at the repository root, alongside `requirements/`.
- `requirements/` documents the application's required behavior. It was
  originally reverse-engineered from a legacy implementation that has
  since been removed from this repository; it now stands on its own as
  the specification against which the application is built and verified.

## C# code style

The two rules below apply identically to `if`/`else`, `for`, and `foreach`.

- A single-statement `if` body MUST NOT be wrapped in `{ }`:
  ```csharp
  if (Directory.Exists(path))
      return new FileStat(0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero), IsDirectory: true);
  ```
  not
  ```csharp
  if (Directory.Exists(path))
  {
      return new FileStat(0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero), IsDirectory: true);
  }
  ```
  An `else` branch with multiple statements MAY keep its `{ }` even when the
  matching `if` branch is a single unbraced statement.
- Never put the condition and its single-statement body of an `if` on the
  same line. Always break after the condition, even when the body is a
  single short statement:
  ```csharp
  if (line.Length == 0)
      continue;
  ```
  not
  ```csharp
  if (line.Length == 0) continue;
  ```
- The same applies to `for` and `foreach`: no braces for a single-statement
  body, and the body always starts on its own line:
  ```csharp
  foreach (var name in knownSessionNames)
      conflicts[name] = new List<ConflictRecord>();
  ```
  not
  ```csharp
  foreach (var name in knownSessionNames) conflicts[name] = new List<ConflictRecord>();
  ```

## Where to look first

Always start with [requirements/README.md](requirements/README.md) — it
indexes every requirements document, in the recommended reading order. In
particular:

- **[requirements/03-tray-icon-requirements.md](requirements/03-tray-icon-requirements.md)**
  is the most important document in this repository. The tray icon showing
  real-time synchronization status is the single most important
  requirement of the whole application — treat any change touching status
  computation, polling, or the tray icon with the care that priority
  implies.

## Working in `requirements/`

- These are analysis documents (functional requirements, non-functional
  requirements). They describe the behavior the .NET application must
  implement.
- If you find a discrepancy between a requirements doc and the actual
  application behavior, prefer re-reading the running application and
  correcting the doc — the running app is the ground truth for *current*
  behavior. Known, intentionally-documented defects (see
  `requirements/03-tray-icon-requirements.md` §7) are the exception: do
  not "fix" the doc to match a behavior that's actually a bug.

## The .NET WPF rewrite

The .NET project (`MutagenMon.sln`, `MutagenMon.App/`, `MutagenMon.Core/`,
`MutagenMon.Core.Tests/`) lives at the repository root (see `README.md`).
The full phased rewrite — tray icon, polling, status classification,
dialogs/menu, conflict resolution, notifications/auto-resolve,
auto-recovery, logging — is implemented and build/test-green.

- Preserve configuration key names/defaults documented in
  `requirements/06-configuration-reference.md` unless the user explicitly
  asks to redesign the config schema.
- Tray icon bitmaps live in `requirements/icons/` as `.ico` files (the
  format actually loaded at runtime), including generated placeholders
  for the three that were missing even in the legacy app. Replace the 3
  placeholders (`green-scan`,
  `green-timeout-white`, `green-timeout-red`) with properly designed
  assets before release — see
  `requirements/03-tray-icon-requirements.md` §3.1/§7.1.
