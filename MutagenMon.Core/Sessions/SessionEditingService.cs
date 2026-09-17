using Microsoft.Extensions.Logging;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Sessions;

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
/// some sessions files carry) is left untouched. The pure line-array
/// methods below do the actual work so they're unit-testable without
/// touching disk; the `*File` methods are thin `File.ReadAllLines` /
/// `File.WriteAllLines` wrappers around them, mirroring
/// <see cref="SessionDefinitionLoader.ParseFile"/>'s split.
/// </summary>
public sealed class SessionEditingService
{
    private readonly MutagenCliClient _cliClient;
    private readonly string _sessionsBatFilePath;
    private readonly ILogger<SessionEditingService> _logger;

    public SessionEditingService(MutagenCliClient cliClient, string sessionsBatFilePath, ILogger<SessionEditingService> logger)
    {
        _cliClient = cliClient;
        _sessionsBatFilePath = sessionsBatFilePath;
        _logger = logger;
    }

    /// <summary>FR-27.4 (Add): create the session live, then append its
    /// line. If the create call fails (FR-27.4.4 — invalid flag
    /// combination, unreachable endpoint, etc.), the exception propagates
    /// and the file is left untouched.</summary>
    public async Task AddAsync(SessionCommandLine model, CancellationToken cancellationToken)
    {
        var rawLine = SessionCommandLineParser.Render(model);
        await _cliClient.CreateSessionAsync(rawLine, cancellationToken);
        AppendSessionToFile(_sessionsBatFilePath, rawLine);
    }

    /// <summary>FR-27.4 (Edit): terminate the session under its old name
    /// (tolerating failure, exactly like FR-13.5's automatic restart — an
    /// already-gone session must not block the recreate), recreate it from
    /// the edited model, then replace its line in place — keyed by
    /// <paramref name="oldName"/> so a rename (the Name field changed)
    /// still finds and replaces the right line. If the create call fails,
    /// the exception propagates and the file is left untouched
    /// (FR-27.4.4).</summary>
    public async Task EditAsync(string oldName, SessionCommandLine model, CancellationToken cancellationToken)
    {
        try
        {
            await _cliClient.TerminateSessionAsync(oldName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to terminate session '{SessionName}' before recreating it during Edit", oldName);
        }

        var rawLine = SessionCommandLineParser.Render(model);
        await _cliClient.CreateSessionAsync(rawLine, cancellationToken);
        ReplaceSessionInFile(_sessionsBatFilePath, oldName, rawLine);
    }

    /// <summary>FR-17.4 (Delete): terminate the session (tolerating
    /// failure, same as Edit), then remove its line. A failure removing the
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

    private static void AppendSessionToFile(string path, string rawCreateCommand) =>
        File.WriteAllLines(path, AppendSession(File.ReadAllLines(path), rawCreateCommand));

    private static void ReplaceSessionInFile(string path, string name, string newRawCreateCommand) =>
        File.WriteAllLines(path, ReplaceSession(File.ReadAllLines(path), name, newRawCreateCommand));

    private static void RemoveSessionFromFile(string path, string name) =>
        File.WriteAllLines(path, RemoveSession(File.ReadAllLines(path), name));

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
