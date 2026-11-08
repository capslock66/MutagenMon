namespace MutagenMon.Core.Ssh;

/// <summary>Quoting of values for the remote POSIX shell. Shared by the
/// conflict file operations (FR-9) and the script macros (<c>|quote</c>,
/// FR-55.4).</summary>
internal static class ShellQuoting
{
    /// <summary>Quotes a value as ONE literal word for the remote POSIX
    /// shell. ssh joins its arguments into a string that the remote shell
    /// re-parses, so the local <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// offers no protection there. Inside single quotes nothing is special
    /// except the single quote itself, which is written as <c>'\''</c>
    /// (close quote, escaped quote, reopen quote).
    /// Problematic file names, and their quoted form:
    /// <c>it's.txt</c> → <c>'it'\''s.txt'</c>;
    /// <c>a'; rm -rf ~; echo '.txt</c> → <c>'a'\''; rm -rf ~; echo '\''.txt'</c>
    /// (without the escaping, the middle part would run as a command);
    /// <c>$(reboot).txt</c> and <c>`id`.txt</c> stay inert inside the quotes.</summary>
    internal static string Quote(string value) =>
        "'" + EscapeSingleQuotes(value) + "'";

    /// <summary>The value with each single quote written as <c>'\''</c>, without
    /// the outer quotes: <c>pa'ss</c> → <c>pa'\''ss</c>. It is a substring of
    /// every quoted form of the value, including inside a longer word that
    /// bash quotes as a whole (<c>set -x</c> traces <c>-ppa'ss</c> as
    /// <c>'-ppa'\''ss'</c>).</summary>
    internal static string EscapeSingleQuotes(string value) =>
        value.Replace("'", "'\\''");
}
