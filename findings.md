# Findings

## Repository
- MutagenMon is a Windows WPF desktop app (README); bash scripts cannot run locally, hence SSH to a Linux host.
- Existing SSH building blocks in `MutagenMon.Core/Ssh/`: `SshConfigParser`, `SshConnectionResolver` (resolves alias from `~/.ssh/config`, default keys under `~/.ssh`).
- `MutagenMonOptions.SshPath` = path to the ssh binary.
- `ConflictFileClient` already launches `ssh` via `ProcessStartInfo.ArgumentList` (no shell quoting) and is designed to be testable with a fake ssh.
- UI: tab pattern exists (`LogsView`, `MutagenConfigEditorView`, `MutagenMonitorConfigEditorView`); `TabControl` style `LeftTabItem` in `App.xaml`.
- `FileLoggerProvider` available for execution logging; `ProfileWatch` is the existing file-watch pattern (not used: reload on demand).
- `requirements/` is the spec; documents are numbered 01..10, next is 11. `UserTests.md` must be updated per implementation.
- Build on Linux: build each .csproj individually, not `MutagenMon.sln`.

## Design analysis
- Script sent as a whole on stdin of `ssh <host> bash -s`; bash runs lines sequentially, each waits for the previous; state persists. App only sees global output stream + final exit code.
- Line-by-line driving by the app (persistent shell, sentinel markers, SSH.NET ShellStream) rejected: needs a shell parser for multi-line constructs, higher cost, benefit limited to per-line progress.
- Closing the SSH connection is safe when the script starts `tmux new -d`; the session survives logout. `tmux new -s` without `-d` fails without a TTY ("open terminal failed: not a terminal").
- `claude --remote-control` is driven from claude.ai / mobile app; terminal view is optional. `tmux capture-pane -p -t <session>` gives a one-shot view; `ssh -t host tmux attach -t <session>` for interactive view.
- Non-interactive ssh shell may not load `.bashrc` (PATH); the first script uses absolute `$HOME/...` paths so it is compatible.
- Security: scripts are user-authored in a local file (same trust level as `~/.ssh/config`); no external interpolation; no secrets in the file; optional `@confirm` directive.

## First script (reference)
```
[appman-remote-control]
# @host: <ssh alias>
cd sources
cd appman
CCD="$HOME/.claude/remote/ccd-cli/$(ls -1 "$HOME/.claude/remote/ccd-cli" | sort -V | tail -1)"
echo "$CCD"
"$CCD" --version
tmux has-session -t appman-rc 2>/dev/null || tmux new -d -s appman-rc "$CCD --remote-control robbie-appman-rc"
```
