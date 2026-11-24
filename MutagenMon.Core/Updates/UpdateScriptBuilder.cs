namespace MutagenMon.Core.Updates;

/// <summary>FR-66.7: builds the batch script that replaces the installed
/// files once the running MutagenMon process has exited (Windows refuses to
/// overwrite an executable in use), then starts MutagenMon again.</summary>
public static class UpdateScriptBuilder
{
    /// <summary>The only subfolder copied besides the top-level files (same
    /// rule as build-release-deploy.bat): <c>config</c> and <c>mutagen</c> hold
    /// each user's own settings and are never overwritten.</summary>
    public const string AssetsFolder = "Assets";

    /// <param name="processId">Process to wait for before copying.</param>
    /// <param name="deployDir">Source folder (the configured deploy path).</param>
    /// <param name="installDir">Folder of the running installation.</param>
    /// <param name="exePath">Full path of the executable to start again.</param>
    /// <param name="logPath">File the script writes its outcome to.</param>
    public static string Build(int processId, string deployDir, string installDir, string exePath, string logPath)
    {
        deployDir = Normalize(deployDir);
        installDir = Normalize(installDir);
        // CRLF on purpose, and labels/goto are fine in a script written with real CRLF endings.
        var lines = new[]
        {
            "@echo off",
            // The file is UTF-8: without this, cmd.exe reads it in the OEM code page and a path with an accent (e.g. a
            // user profile name in %TEMP%) is garbled, which makes the redirection to the log, and so the copy, fail.
            "chcp 65001 >nul",
            "setlocal",
            $"set \"MM_LOG={Escape(logPath)}\"",
            "echo %date% %time% update started > \"%MM_LOG%\"",
            "set /a tries=0",
            ":wait",
            $"tasklist /FI \"PID eq {processId}\" /NH | find \"{processId}\" >nul",
            "if errorlevel 1 goto copy",
            "set /a tries+=1",
            "if %tries% geq 60 goto timeout",
            // ping, not timeout: timeout refuses to run when its input is not a console.
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":timeout",
            // Not restarted: the old instance still holds the single-instance mutex, so a new one would exit at once.
            "echo %date% %time% MutagenMon did not exit within 60 s; nothing copied, not restarted >> \"%MM_LOG%\"",
            "goto end",
            ":copy",
            // Nothing is copied when the source is incomplete: a partial copy would mix two builds.
            $"if not exist {Quote(deployDir + "\\" + UpdateChecker.ExeName)} goto failed",
            $"if not exist {Quote(deployDir + "\\" + AssetsFolder)} goto failed",
            // Top-level files only (no /E): config\ and mutagen\ are never touched. Exit codes below 8 are success.
            $"robocopy {Quote(deployDir)} {Quote(installDir)} *.* /IS /IT /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >> \"%MM_LOG%\" 2>&1",
            "if errorlevel 8 goto failed",
            $"robocopy {Quote(deployDir + "\\" + AssetsFolder)} {Quote(installDir + "\\" + AssetsFolder)} /E /IS /IT /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >> \"%MM_LOG%\" 2>&1",
            "if errorlevel 8 goto failed",
            "echo %date% %time% files copied >> \"%MM_LOG%\"",
            "goto start",
            ":failed",
            "echo %date% %time% copy failed or incomplete (the installation may be partly updated); restarting >> \"%MM_LOG%\"",
            ":start",
            $"start \"\" {Quote(exePath)}",
            ":end",
            // Deletes the script itself; the (goto) idiom lets cmd.exe remove a running batch file.
            "(goto) 2>nul & del \"%~f0\"",
        };
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string Normalize(string dir) => dir.Trim().TrimEnd('\\', '/');

    private static string Quote(string value) => "\"" + Escape(value) + "\"";

    /// <summary>Doubles the percent signs (batch variable syntax) and
    /// rejects double quotes, which cannot appear in a Windows path.</summary>
    private static string Escape(string value)
    {
        if (value.Contains('"'))
            throw new ArgumentException($"A path used in the update script must not contain a double quote: {value}", nameof(value));
        return value.Replace("%", "%%");
    }
}
