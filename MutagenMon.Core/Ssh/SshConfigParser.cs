namespace MutagenMon.Core.Ssh;

/// <summary>Resolved `~/.ssh/config` fields for a single `Host` alias —
/// enough to build a <c>Renci.SshNet.ConnectionInfo</c> in
/// <see cref="SshConnectionResolver"/> without SSH.NET itself having any
/// notion of the OpenSSH config file format.</summary>
public sealed record SshConfigEntry(string? HostName, string? User, int? Port, string? IdentityFile);

/// <summary>Minimal, dependency-free parser for the subset of OpenSSH's
/// `~/.ssh/config` syntax this app needs: exact (non-wildcard) `Host`
/// alias matching, and the `HostName`/`User`/`Port`/`IdentityFile` keys
/// within a matching block. Anything else in the file (other keys,
/// wildcard patterns, `Match` blocks, `Include`) is ignored.</summary>
public static class SshConfigParser
{
    public static SshConfigEntry Parse(IEnumerable<string> configLines, string host)
    {
        string? hostName = null;
        string? user = null;
        int? port = null;
        string? identityFile = null;
        var inMatchingBlock = false;

        foreach (var rawLine in configLines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var (key, value) = SplitKeyValue(line);
            if (key is null)
                continue;

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                inMatchingBlock = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(pattern => pattern.Equals(host, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (!inMatchingBlock)
                continue;

            if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase))
                hostName = value;
            else if (key.Equals("User", StringComparison.OrdinalIgnoreCase))
                user = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var parsedPort))
                port = parsedPort;
            else if (key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
                identityFile = value;
        }

        return new SshConfigEntry(hostName, user, port, identityFile);
    }

    /// <summary>OpenSSH accepts both `Key value` and `Key=value` (optionally
    /// surrounded by whitespace); this covers both without a regex.</summary>
    private static (string? Key, string Value) SplitKeyValue(string line)
    {
        var separatorIndex = line.IndexOfAny(new[] { ' ', '\t', '=' });
        if (separatorIndex <= 0)
            return (null, "");

        var key = line[..separatorIndex];
        var value = line[(separatorIndex + 1)..].Trim().TrimStart('=').Trim().Trim('"');
        return (key, value);
    }
}
