using System.Text;

namespace MutagenMon.Core.Sessions;

/// <summary>
/// Converts between a raw `mutagen sync create ...` line (as stored in
/// `mutagen-create.bat`, FR-1.1) and the structured <see
/// cref="SessionCommandLine"/> the Add/Edit window (FR-18 through FR-26)
/// binds to. See requirements/07-session-management-requirements.md
/// FR-27 for the round-trip rules this implements.
///
/// The line's first three tokens ("mutagen sync create") are assumed
/// fixed, matching every other component that already relies on this
/// shape (<see cref="SessionDefinitionLoader"/>, FR-1.1).
/// </summary>
public static class SessionCommandLineParser
{
    public static SessionCommandLine Parse(string line)
    {
        var tokens = Tokenize(line);
        var result = new SessionCommandLine();
        var unknown = new List<UnknownFlag>();
        var positionalsSeen = 0;

        for (var i = 3; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-'))
            {
                if (positionalsSeen == 0)
                {
                    result.Alpha = token;
                    positionalsSeen = 1;
                }
                else if (positionalsSeen == 1)
                {
                    result.Beta = token;
                    positionalsSeen = 2;
                }
                else
                    unknown.Add(new UnknownFlag(Quote(token)));
                continue;
            }

            var (flagName, inlineValue) = SplitFlag(token);

            if (flagName is "--ignore-vcs" or "--no-ignore-vcs")
            {
                result.IgnoreVcs = flagName == "--ignore-vcs";
                continue;
            }

            var value = inlineValue;
            var consumedNext = false;
            if (value is null && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
            {
                value = tokens[i + 1];
                consumedNext = true;
            }

            if (value is not null && TryApplyValueFlag(result, flagName, value))
            {
                if (consumedNext)
                    i++;
                continue;
            }

            if (consumedNext)
            {
                unknown.Add(new UnknownFlag($"{token} {Quote(value!)}"));
                i++;
            }
            else
                unknown.Add(new UnknownFlag(token));
        }

        result.UnknownFlags = unknown;
        return result;
    }

    public static string Render(SessionCommandLine model)
    {
        var parts = new List<string>
        {
            "mutagen", "sync", "create", Quote(model.Alpha), Quote(model.Beta), $"--name={model.Name}",
        };

        if (model.Mode != SyncMode.TwoWaySafe)
            parts.Add($"-m {model.Mode.ToFlagValue()}");

        foreach (var pattern in model.Ignores)
            if (pattern.Length > 0)
                parts.Add($"-i {Quote(pattern)}");

        if (model.IgnoreVcs is { } ignoreVcs)
            parts.Add(ignoreVcs ? "--ignore-vcs" : "--no-ignore-vcs");

        if (model.IgnoreSyntaxDocker)
            parts.Add("--ignore-syntax=docker");

        if (model.PermissionsMode != PermissionsMode.Portable)
            parts.Add($"--permissions-mode={model.PermissionsMode.ToFlagValue()}");

        AppendPerSideString(parts, "--default-owner", model.Owner);
        AppendPerSideString(parts, "--default-group", model.Group);
        AppendPerSideString(parts, "--default-file-mode", model.FileMode);
        AppendPerSideString(parts, "--default-directory-mode", model.DirectoryMode);

        if (model.SymlinkMode != SymlinkMode.Portable)
            parts.Add($"--symlink-mode={model.SymlinkMode.ToFlagValue()}");

        AppendPerSideEnum(parts, "--watch-mode", model.WatchMode, SessionCommandLineEnumFormatting.ToFlagValue, WatchMode.Portable);
        AppendPerSideInt(parts, "--watch-polling-interval", model.WatchPollingInterval);
        AppendPerSideEnum(parts, "--probe-mode", model.ProbeMode, SessionCommandLineEnumFormatting.ToFlagValue, ProbeMode.Probe);
        AppendPerSideEnum(parts, "--scan-mode", model.ScanMode, SessionCommandLineEnumFormatting.ToFlagValue, ScanMode.Accelerated);
        AppendPerSideEnum(parts, "--stage-mode", model.StageMode, SessionCommandLineEnumFormatting.ToFlagValue, StageMode.Mutagen);

        if (model.MaxStagingFileSize is { Length: > 0 } size)
            parts.Add($"--max-staging-file-size={Quote(size)}");
        if (model.MaxEntryCount is { } maxEntryCount)
            parts.Add($"--max-entry-count={maxEntryCount}");

        foreach (var flag in model.UnknownFlags)
            if (flag.Keep)
                parts.Add(flag.Text);

        return string.Join(' ', parts);
    }

    private static bool TryApplyValueFlag(SessionCommandLine r, string flag, string value)
    {
        switch (flag)
        {
            case "--name":
            case "-n":
                r.Name = value;
                return true;
            case "--mode":
            case "-m":
            case "--sync-mode":
                if (ParseSyncMode(value) is not { } mode)
                    return false;
                r.Mode = mode;
                return true;
            case "-i":
            case "--ignore":
                foreach (var pattern in value.Split(','))
                    if (pattern.Length > 0)
                        r.Ignores.Add(pattern);
                return true;
            case "--ignore-syntax":
                if (value == "docker")
                    r.IgnoreSyntaxDocker = true;
                else if (value == "mutagen")
                    r.IgnoreSyntaxDocker = false;
                else
                    return false;
                return true;
            case "--permissions-mode":
                if (ParsePermissionsMode(value) is not { } permissionsMode)
                    return false;
                r.PermissionsMode = permissionsMode;
                return true;
            case "--default-owner":
                r.Owner = r.Owner with { Session = value };
                return true;
            case "--default-owner-alpha":
                r.Owner = r.Owner with { Alpha = value };
                return true;
            case "--default-owner-beta":
                r.Owner = r.Owner with { Beta = value };
                return true;
            case "--default-group":
                r.Group = r.Group with { Session = value };
                return true;
            case "--default-group-alpha":
                r.Group = r.Group with { Alpha = value };
                return true;
            case "--default-group-beta":
                r.Group = r.Group with { Beta = value };
                return true;
            case "--default-file-mode":
                r.FileMode = r.FileMode with { Session = value };
                return true;
            case "--default-file-mode-alpha":
                r.FileMode = r.FileMode with { Alpha = value };
                return true;
            case "--default-file-mode-beta":
                r.FileMode = r.FileMode with { Beta = value };
                return true;
            case "--default-directory-mode":
                r.DirectoryMode = r.DirectoryMode with { Session = value };
                return true;
            case "--default-directory-mode-alpha":
                r.DirectoryMode = r.DirectoryMode with { Alpha = value };
                return true;
            case "--default-directory-mode-beta":
                r.DirectoryMode = r.DirectoryMode with { Beta = value };
                return true;
            case "--symlink-mode":
                if (ParseSymlinkMode(value) is not { } symlinkMode)
                    return false;
                r.SymlinkMode = symlinkMode;
                return true;
            case "--watch-mode":
                if (ParseWatchMode(value) is not { } watchMode)
                    return false;
                r.WatchMode = r.WatchMode with { Session = watchMode };
                return true;
            case "--watch-mode-alpha":
                if (ParseWatchMode(value) is not { } watchModeAlpha)
                    return false;
                r.WatchMode = r.WatchMode with { Alpha = watchModeAlpha };
                return true;
            case "--watch-mode-beta":
                if (ParseWatchMode(value) is not { } watchModeBeta)
                    return false;
                r.WatchMode = r.WatchMode with { Beta = watchModeBeta };
                return true;
            case "--watch-polling-interval":
                if (!int.TryParse(value, out var watchPollingInterval))
                    return false;
                r.WatchPollingInterval = r.WatchPollingInterval with { Session = watchPollingInterval };
                return true;
            case "--watch-polling-interval-alpha":
                if (!int.TryParse(value, out var watchPollingIntervalAlpha))
                    return false;
                r.WatchPollingInterval = r.WatchPollingInterval with { Alpha = watchPollingIntervalAlpha };
                return true;
            case "--watch-polling-interval-beta":
                if (!int.TryParse(value, out var watchPollingIntervalBeta))
                    return false;
                r.WatchPollingInterval = r.WatchPollingInterval with { Beta = watchPollingIntervalBeta };
                return true;
            case "--probe-mode":
                if (ParseProbeMode(value) is not { } probeMode)
                    return false;
                r.ProbeMode = r.ProbeMode with { Session = probeMode };
                return true;
            case "--probe-mode-alpha":
                if (ParseProbeMode(value) is not { } probeModeAlpha)
                    return false;
                r.ProbeMode = r.ProbeMode with { Alpha = probeModeAlpha };
                return true;
            case "--probe-mode-beta":
                if (ParseProbeMode(value) is not { } probeModeBeta)
                    return false;
                r.ProbeMode = r.ProbeMode with { Beta = probeModeBeta };
                return true;
            case "--scan-mode":
                if (ParseScanMode(value) is not { } scanMode)
                    return false;
                r.ScanMode = r.ScanMode with { Session = scanMode };
                return true;
            case "--scan-mode-alpha":
                if (ParseScanMode(value) is not { } scanModeAlpha)
                    return false;
                r.ScanMode = r.ScanMode with { Alpha = scanModeAlpha };
                return true;
            case "--scan-mode-beta":
                if (ParseScanMode(value) is not { } scanModeBeta)
                    return false;
                r.ScanMode = r.ScanMode with { Beta = scanModeBeta };
                return true;
            case "--stage-mode":
                if (ParseStageMode(value) is not { } stageMode)
                    return false;
                r.StageMode = r.StageMode with { Session = stageMode };
                return true;
            case "--stage-mode-alpha":
                if (ParseStageMode(value) is not { } stageModeAlpha)
                    return false;
                r.StageMode = r.StageMode with { Alpha = stageModeAlpha };
                return true;
            case "--stage-mode-beta":
                if (ParseStageMode(value) is not { } stageModeBeta)
                    return false;
                r.StageMode = r.StageMode with { Beta = stageModeBeta };
                return true;
            case "--max-staging-file-size":
                r.MaxStagingFileSize = value;
                return true;
            case "--max-entry-count":
                if (!long.TryParse(value, out var maxEntryCount))
                    return false;
                r.MaxEntryCount = maxEntryCount;
                return true;
            default:
                return false;
        }
    }

    private static void AppendPerSideString(List<string> parts, string flagName, PerSideText value)
    {
        if (value.Session is { Length: > 0 } session)
            parts.Add($"{flagName}={Quote(session)}");
        if (value.Alpha is { Length: > 0 } alpha)
            parts.Add($"{flagName}-alpha={Quote(alpha)}");
        if (value.Beta is { Length: > 0 } beta)
            parts.Add($"{flagName}-beta={Quote(beta)}");
    }

    private static void AppendPerSideInt(List<string> parts, string flagName, PerSide<int> value)
    {
        if (value.Session is { } session)
            parts.Add($"{flagName}={session}");
        if (value.Alpha is { } alpha)
            parts.Add($"{flagName}-alpha={alpha}");
        if (value.Beta is { } beta)
            parts.Add($"{flagName}-beta={beta}");
    }

    private static void AppendPerSideEnum<T>(
        List<string> parts, string flagName, PerSide<T> value, Func<T, string> format, T defaultValue)
        where T : struct, Enum
    {
        if (value.Session is { } session && !session.Equals(defaultValue))
            parts.Add($"{flagName}={format(session)}");
        if (value.Alpha is { } alpha && !alpha.Equals(defaultValue))
            parts.Add($"{flagName}-alpha={format(alpha)}");
        if (value.Beta is { } beta && !beta.Equals(defaultValue))
            parts.Add($"{flagName}-beta={format(beta)}");
    }

    private static (string Name, string? InlineValue) SplitFlag(string token)
    {
        var eq = token.IndexOf('=');
        return eq < 0 ? (token, null) : (token[..eq], token[(eq + 1)..]);
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>Splits on whitespace like a shell would, treating a `"`
    /// as toggling "inside a quoted span" rather than only recognizing a
    /// fully self-quoted token — so `--flag="value with spaces"` tokenizes
    /// as one token (quotes stripped), not two broken ones split on the
    /// space inside the quotes.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static SyncMode? ParseSyncMode(string value) => value switch
    {
        "two-way-safe" => SyncMode.TwoWaySafe,
        "two-way-resolved" => SyncMode.TwoWayResolved,
        "one-way-safe" => SyncMode.OneWaySafe,
        "one-way-replica" => SyncMode.OneWayReplica,
        _ => null,
    };

    private static SymlinkMode? ParseSymlinkMode(string value) => value switch
    {
        "portable" => SymlinkMode.Portable,
        "ignore" => SymlinkMode.Ignore,
        "posix-raw" => SymlinkMode.PosixRaw,
        _ => null,
    };

    private static WatchMode? ParseWatchMode(string value) => value switch
    {
        "portable" => WatchMode.Portable,
        "force-poll" => WatchMode.ForcePoll,
        "no-watch" => WatchMode.NoWatch,
        _ => null,
    };

    private static ProbeMode? ParseProbeMode(string value) => value switch
    {
        "probe" => ProbeMode.Probe,
        "assume" => ProbeMode.Assume,
        _ => null,
    };

    private static ScanMode? ParseScanMode(string value) => value switch
    {
        "accelerated" => ScanMode.Accelerated,
        "full" => ScanMode.Full,
        _ => null,
    };

    private static StageMode? ParseStageMode(string value) => value switch
    {
        "mutagen" => StageMode.Mutagen,
        "neighboring" => StageMode.Neighboring,
        _ => null,
    };

    private static PermissionsMode? ParsePermissionsMode(string value) => value switch
    {
        "portable" => PermissionsMode.Portable,
        "manual" => PermissionsMode.Manual,
        _ => null,
    };
}
