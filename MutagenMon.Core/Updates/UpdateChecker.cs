namespace MutagenMon.Core.Updates;

/// <summary>Outcome of one update check (FR-66.2).</summary>
/// <param name="IsAvailable">True when the deployed executable is newer than the running one.</param>
/// <param name="DeployedExePath">Full path of the deployed executable, or null when none was found.</param>
/// <param name="DeployedWriteTimeUtc">Last write time of the deployed executable, when found.</param>
/// <param name="CurrentWriteTimeUtc">Last write time of the running executable, when known.</param>
/// <param name="FailureReason">Why nothing could be compared (folder or file missing, I/O error), for the log; null when the check ran or the feature is disabled.</param>
public sealed record UpdateCheckResult(
    bool IsAvailable,
    string? DeployedExePath,
    DateTime? DeployedWriteTimeUtc,
    DateTime? CurrentWriteTimeUtc,
    string? FailureReason = null)
{
    public static UpdateCheckResult None { get; } = new(false, null, null, null);
}

/// <summary>FR-66.2: compares the last write time of
/// <c>MutagenMon.exe</c> in the deploy folder with the one of the running
/// executable.</summary>
public static class UpdateChecker
{
    public const string ExeName = "MutagenMon.exe";

    /// <summary>Never throws for an empty, missing or unreachable folder:
    /// those simply mean "no update known" (FR-66.3). Blocking I/O (the
    /// folder may be a network share): call it off the UI thread.</summary>
    /// <param name="baseDirectory">Folder a relative <paramref name="deployPath"/> is resolved against (the application folder, like the other configured paths).</param>
    public static UpdateCheckResult Check(string? deployPath, string? runningExePath, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(deployPath) || string.IsNullOrWhiteSpace(runningExePath))
            return UpdateCheckResult.None;

        try
        {
            var deployDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(deployPath.Trim()), baseDirectory);
            var deployedExe = Path.Combine(deployDir, ExeName);
            if (!Directory.Exists(deployDir))
                return UpdateCheckResult.None with { FailureReason = $"the deploy folder '{deployDir}' was not found or is unreachable" };
            if (!File.Exists(deployedExe))
                return UpdateCheckResult.None with { FailureReason = $"'{deployedExe}' was not found" };
            if (!File.Exists(runningExePath))
                return UpdateCheckResult.None with { FailureReason = $"the running executable '{runningExePath}' was not found" };

            var deployedTime = File.GetLastWriteTimeUtc(deployedExe);
            var currentTime = File.GetLastWriteTimeUtc(runningExePath);
            return new UpdateCheckResult(deployedTime > currentTime, deployedExe, deployedTime, currentTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return UpdateCheckResult.None with { FailureReason = ex.Message };
        }
    }
}
