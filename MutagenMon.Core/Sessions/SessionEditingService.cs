using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Sessions;

/// <summary>What <see cref="SessionEditingService.EditAsync"/> did to the
/// running session (FR-65.3): the caller reloads the monitor only after
/// <see cref="Recreated"/>.</summary>
public enum SessionEditOutcome
{
    /// <summary>The session was terminated and created again from the edited model.</summary>
    Recreated,

    /// <summary>Only the `@halted-auto-fix` directive changed: the session was left running untouched.</summary>
    DirectiveOnly,
}

/// <summary>
/// Implements the Add/Edit/Delete actions FR-17/FR-27 offer from the
/// status view: orchestrates the live `mutagen` CLI call together with
/// the `mutagen-create.bat` file mutation, in the order FR-27.4/FR-17.4
/// require. Presentation (dialogs, grid refresh — FR-27.5/FR-17.5) is an
/// App-layer concern; this only throws on an unrecoverable failure for the
/// caller to surface.
///
/// The three ways a session's line in `mutagen-create.bat` changes
/// (FR-17.4, FR-27.4): append a new one, replace an existing one in
/// place (same position — never moved to the end), or remove one
/// entirely. Everything else in the file (comments, blank lines, the
/// `mutagen sync terminate --all`/`mutagen sync list` housekeeping lines
/// some sessions files carry) is left untouched, except the session's
/// `@halted-auto-fix` directive line, which these actions also write, rename
/// or remove (FR-64, FR-65). The pure line-array
/// methods below do the actual work so they're unit-testable without
/// touching disk; the `*File` methods are thin `File.ReadAllLines` /
/// `File.WriteAllLines` wrappers around them, mirroring
/// <see cref="SessionDefinitionLoader.ParseFile"/>'s split.
/// </summary>
public sealed partial class SessionEditingService
{
    private readonly MutagenCliClient _cliClient;
    private readonly string _sessionsBatFilePath;
    private readonly ILogger<SessionEditingService> _logger;

    // Matches the `mutagen sync terminate --all` housekeeping line that anchors the directive block (FR-64.3). Only the
    // plain form is recognised (not "call mutagen ...", "@mutagen ..." or "mutagen.exe ..."): any other form falls back
    // to the first session line, which still gives a valid, idempotent result.
    [GeneratedRegex(@"^\s*mutagen\s+sync\s+terminate\s+--all\b", RegexOptions.IgnoreCase)]
    private static partial Regex TerminateAllRegex();

    public SessionEditingService(MutagenCliClient cliClient, string sessionsBatFilePath, ILogger<SessionEditingService> logger)
    {
        _cliClient = cliClient;
        _sessionsBatFilePath = sessionsBatFilePath;
        _logger = logger;
    }

    /// <summary>FR-27.4 (Add): create the session live, then append its
    /// line (and its `@halted-auto-fix` directive when the model sets one,
    /// FR-65.2; with "inherit" it instead removes an orphan directive of the
    /// same name, FR-64.5, and the file's directives are regrouped as one
    /// block, FR-64.3). If the create call fails (FR-27.4.4 — invalid flag
    /// combination, unreachable endpoint, etc.), the exception propagates
    /// and the file is left untouched.</summary>
    public async Task AddAsync(SessionCommandLine model, CancellationToken cancellationToken)
    {
        var rawLine = SessionCommandLineParser.Render(model);
        await _cliClient.CreateSessionAsync(rawLine, cancellationToken);

        // One single write for the line and its directive: a failure between two writes would leave the session
        // line in the file while the caller reports a failure (and a retry would create the session twice).
        // The directive is applied even for "inherit" (null): it then removes an orphan directive of the same name
        // left in the file by a session line that was deleted by hand (FR-64.5), so it cannot come back to life.
        var lines = AppendSession(File.ReadAllLines(_sessionsBatFilePath), rawLine);
        File.WriteAllLines(_sessionsBatFilePath, ApplyHaltedAutoFix(lines, model.Name, model.Name, model.HaltedAutoFix));
    }

    /// <summary>FR-27.4 (Edit): terminate the session under its old name
    /// (tolerating failure, exactly like FR-13.5's automatic restart — an
    /// already-gone session must not block the recreate), recreate it from
    /// the edited model, then replace its line in place — keyed by
    /// <paramref name="oldName"/> so a rename (the Name field changed)
    /// still finds and replaces the right line. If the create call fails,
    /// the exception propagates and the file is left untouched
    /// (FR-27.4.4).
    ///
    /// FR-65.3: when nothing but the `@halted-auto-fix` directive changed
    /// (same name, and the stored line gives the same command once parsed
    /// and rendered again), the session is NOT terminated or recreated —
    /// that would reset its history — and only the directive is
    /// rewritten.</summary>
    public async Task<SessionEditOutcome> EditAsync(string oldName, SessionCommandLine model, CancellationToken cancellationToken)
    {
        var rawLine = SessionCommandLineParser.Render(model);
        if (IsDirectiveOnlyChange(oldName, model.Name, rawLine))
        {
            ApplyHaltedAutoFixToFile(_sessionsBatFilePath, oldName, model.Name, model.HaltedAutoFix);
            return SessionEditOutcome.DirectiveOnly;
        }

        try
        {
            await _cliClient.TerminateSessionAsync(oldName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to terminate session '{SessionName}' before recreating it during Edit", oldName);
        }

        await _cliClient.CreateSessionAsync(rawLine, cancellationToken);

        // Single write for the replaced line and the renamed/updated directive (see AddAsync).
        var replaced = ReplaceSession(File.ReadAllLines(_sessionsBatFilePath), oldName, rawLine);
        File.WriteAllLines(_sessionsBatFilePath, ApplyHaltedAutoFix(replaced, oldName, model.Name, model.HaltedAutoFix));
        return SessionEditOutcome.Recreated;
    }

    /// <summary>FR-17.4 (Delete): terminate the session (tolerating
    /// failure, same as Edit), then remove its line and its directive
    /// (FR-64.5). A failure removing the
    /// line here is not tolerated (FR-17.5) — it propagates so the caller
    /// can show an error and must not drop the session from the grid.</summary>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _cliClient.TerminateSessionAsync(name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to terminate session '{SessionName}' during Delete", name);
        }

        RemoveSessionFromFile(_sessionsBatFilePath, name);
    }

    private bool IsDirectiveOnlyChange(string oldName, string newName, string newRawLine)
    {
        if (oldName != newName)
            return false;

        var lines = File.ReadAllLines(_sessionsBatFilePath);
        var index = FindLastSessionLineIndex(lines, oldName);
        if (index < 0)
            return false;

        // Both lines go through the same parser and renderer, so a hand-written line that differs from the canonical
        // one only by formatting (flag order, -m vs --sync-mode) still counts as unchanged.
        var stored = SessionCommandLineParser.Parse(lines[index].Trim());

        // A stored line without a mode flag runs with whatever mode mutagen defaults to, which may not be the
        // two-way-safe the window displays: saving must rewrite it with an explicit -m and recreate the session.
        if (!stored.HasExplicitMode)
            return false;

        return SessionCommandLineParser.Render(stored) == newRawLine;
    }

    private static string[] AppendSession(IReadOnlyList<string> lines, string rawCreateCommand) =>
        [.. lines, rawCreateCommand];

    /// <summary>Throws if no session named <paramref name="name"/> is
    /// found — the caller (FR-27.5) is expected to already know it exists,
    /// having just loaded it into the Edit window.</summary>
    private static string[] ReplaceSession(IReadOnlyList<string> lines, string name, string newRawCreateCommand)
    {
        var index = FindLastSessionLineIndex(lines, name);
        if (index < 0)
            throw new InvalidOperationException($"No session named '{name}' found.");

        var result = lines.ToArray();
        result[index] = newRawCreateCommand;
        return result;
    }

    /// <summary>Throws if no session named <paramref name="name"/> is
    /// found (see <see cref="ReplaceSession"/>).</summary>
    private static string[] RemoveSession(IReadOnlyList<string> lines, string name)
    {
        var index = FindLastSessionLineIndex(lines, name);
        if (index < 0)
            throw new InvalidOperationException($"No session named '{name}' found.");

        var result = new List<string>(lines);
        result.RemoveAt(index);
        return result.ToArray();
    }

    /// <summary>FR-64/FR-65: returns the file lines with the `@halted-auto-fix`
    /// directives rewritten for one session. All the directives of the file
    /// are gathered first (last one wins per session, FR-64.4) and written
    /// back as one block (FR-64.3): the directive of
    /// <paramref name="oldName"/> is renamed to <paramref name="newName"/>
    /// keeping its place, then set to <paramref name="value"/> — or removed
    /// when <paramref name="value"/> is null ("inherit"). A directive line
    /// with an invalid value is not a directive and is left where it is.
    /// Every other line is untouched.</summary>
    internal static string[] ApplyHaltedAutoFix(IReadOnlyList<string> lines, string oldName, string newName, bool? value)
    {
        var directives = new List<(string Name, bool Value)>();
        var rest = new List<string>();
        foreach (var line in lines)
        {
            if (!SessionDefinitionLoader.TryParseHaltedAutoFixDirective(line, out var name, out var rawValue)
                || !bool.TryParse(rawValue, out var parsedValue))
            {
                rest.Add(line);
                continue;
            }

            var existing = directives.FindIndex(d_ => d_.Name == name);
            if (existing >= 0)
                directives[existing] = (name, parsedValue);
            else
                directives.Add((name, parsedValue));
        }

        var renamed = directives.FindIndex(d_ => d_.Name == oldName);
        if (renamed >= 0 && oldName != newName)
        {
            directives.RemoveAll(d_ => d_.Name == newName);
            renamed = directives.FindIndex(d_ => d_.Name == oldName);
            directives[renamed] = (newName, directives[renamed].Value);
        }

        var target = directives.FindIndex(d_ => d_.Name == newName);
        if (value is null && target >= 0)
            directives.RemoveAt(target);
        else if (value is { } wanted && target >= 0)
            directives[target] = (newName, wanted);
        else if (value is { } added)
            directives.Add((newName, added));

        if (directives.Count > 0)
            InsertDirectiveBlock(rest, directives.Select(d_ => SessionDefinitionLoader.FormatHaltedAutoFixDirective(d_.Name, d_.Value)).ToList());
        return rest.ToArray();
    }

    /// <summary>FR-64.3: puts the block just above the blank line that
    /// precedes `mutagen sync terminate --all`, with exactly one blank line
    /// between the block and that line (added when missing). Without that
    /// line: before the first session line, else at the end of the
    /// file.</summary>
    private static void InsertDirectiveBlock(List<string> lines, List<string> block)
    {
        // The anchor is the terminate line, else the first session line. The same rule applies to both, which keeps
        // the writer idempotent: a blank line already above the anchor is reused instead of adding another one.
        var anchor = lines.FindIndex(l_ => TerminateAllRegex().IsMatch(l_));
        if (anchor < 0)
            anchor = lines.FindIndex(l_ => SessionDefinitionLoader.TryExtractName(l_, out _, out _));

        if (anchor < 0)
        {
            lines.AddRange(block);
            return;
        }

        if (anchor > 0 && string.IsNullOrWhiteSpace(lines[anchor - 1]))
        {
            lines.InsertRange(anchor - 1, block);
            return;
        }

        block.Add("");
        lines.InsertRange(anchor, block);
    }

    private static void RemoveSessionFromFile(string path, string name)
    {
        var withoutSession = RemoveSession(File.ReadAllLines(path), name);
        File.WriteAllLines(path, ApplyHaltedAutoFix(withoutSession, name, name, null));
    }

    /// <summary>Rewrites the file only when the directives actually change,
    /// so an Edit that changes nothing leaves it alone.</summary>
    private static void ApplyHaltedAutoFixToFile(string path, string oldName, string newName, bool? value)
    {
        var lines = File.ReadAllLines(path);
        var updated = ApplyHaltedAutoFix(lines, oldName, newName, value);
        if (!updated.SequenceEqual(lines))
            File.WriteAllLines(path, updated);
    }

    /// <summary>Last line wins on a duplicate name (FR-1.2) — same
    /// authoritative line <see cref="SessionDefinitionLoader"/> would
    /// report for that name, so Edit/Delete always act on the session the
    /// grid is actually showing.</summary>
    private static int FindLastSessionLineIndex(IReadOnlyList<string> lines, string name)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (SessionDefinitionLoader.TryExtractName(lines[i], out _, out var extractedName) && extractedName == name)
                return i;
        return -1;
    }
}
