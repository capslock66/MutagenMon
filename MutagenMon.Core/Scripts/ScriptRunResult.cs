namespace MutagenMon.Core.Scripts;

public enum ScriptRunOutcome
{
    /// <summary>The ssh process exited by itself; see
    /// <see cref="ScriptRunResult.ExitCode"/>.</summary>
    Completed,
    Cancelled,
    TimedOut,
    /// <summary>The ssh process could not be started (FR-54.1).</summary>
    FailedToStart,
}

/// <summary>One line written by the ssh process, streamed as it arrives
/// (FR-52.1).</summary>
public sealed record ScriptOutputLine(string Text, bool IsError);

/// <summary>How a run ended (FR-52.3). <paramref name="ExitCode"/> is only
/// set for <see cref="ScriptRunOutcome.Completed"/>;
/// <paramref name="Error"/> only for
/// <see cref="ScriptRunOutcome.FailedToStart"/>.</summary>
public sealed record ScriptRunResult(ScriptRunOutcome Outcome, int? ExitCode, TimeSpan Duration, string? Error = null);
