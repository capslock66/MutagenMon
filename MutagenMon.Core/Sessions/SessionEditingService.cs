using Microsoft.Extensions.Logging;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Sessions;

/// <summary>
/// Implements the Add/Edit/Delete actions FR-17/FR-27 offer from the
/// status view: orchestrates the live `mutagen` CLI call together with
/// the `mutagen-create.bat` file mutation (<see cref="SessionFileMutator"/>),
/// in the order FR-27.4/FR-17.4 require. Presentation (dialogs, grid
/// refresh — FR-27.5/FR-17.5) is an App-layer concern; this only throws on
/// an unrecoverable failure for the caller to surface.
/// </summary>
public sealed class SessionEditingService
{
    private readonly IMutagenCliClient _cliClient;
    private readonly string _sessionsBatFilePath;
    private readonly ILogger<SessionEditingService> _logger;

    public SessionEditingService(IMutagenCliClient cliClient, string sessionsBatFilePath, ILogger<SessionEditingService> logger)
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
        SessionFileMutator.AppendSessionToFile(_sessionsBatFilePath, rawLine);
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
        SessionFileMutator.ReplaceSessionInFile(_sessionsBatFilePath, oldName, rawLine);
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

        SessionFileMutator.RemoveSessionFromFile(_sessionsBatFilePath, name);
    }
}
