using Renci.SshNet;

namespace MutagenMon.Core.Ssh;

/// <summary>Builds a <see cref="ConnectionInfo"/> for a bare host alias
/// (e.g. "robbie", the same token used before the ':' in a Mutagen
/// endpoint) the same way the system `ssh`/`scp` binaries already used
/// elsewhere in this app would resolve it: by looking it up in the
/// current user's `~/.ssh/config`, falling back to the alias itself as the
/// actual hostname, the current OS user, port 22, and the default private
/// key filenames under `~/.ssh` when the config doesn't say otherwise.
/// Key-based authentication only — no password/interactive prompt, since
/// this runs from a background picker dialog with no place to prompt for
/// one.</summary>
public static class SshConnectionResolver
{
    private static readonly string[] DefaultIdentityFileNames = { "id_ed25519", "id_rsa", "id_ecdsa" };

    public static ConnectionInfo Resolve(string host)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var configPath = Path.Combine(sshDir, "config");
        var entry = File.Exists(configPath)
            ? SshConfigParser.Parse(File.ReadAllLines(configPath), host)
            : new SshConfigEntry(null, null, null, null);

        var hostName = entry.HostName ?? host;
        var userName = entry.User ?? Environment.UserName;
        var port = entry.Port ?? 22;

        var identityFiles = entry.IdentityFile is { } configuredFile
            ? new[] { ExpandHome(configuredFile) }
            : DefaultIdentityFileNames.Select(name => Path.Combine(sshDir, name));

        var keyFiles = identityFiles
            .Where(File.Exists)
            .Select(path => new PrivateKeyFile(path))
            .ToArray();

        if (keyFiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"No usable SSH private key found for host '{host}'. " +
                "Add an IdentityFile entry for it in ~/.ssh/config, or place a default key " +
                "(id_ed25519, id_rsa or id_ecdsa) under ~/.ssh.");
        }

        return new ConnectionInfo(hostName, port, userName, new PrivateKeyAuthenticationMethod(userName, keyFiles));
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;
}
