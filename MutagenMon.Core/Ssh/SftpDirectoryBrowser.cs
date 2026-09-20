using Renci.SshNet;

namespace MutagenMon.Core.Ssh;

/// <summary>Thin wrapper around <c>Renci.SshNet.SftpClient</c> for the
/// Add/Edit session window's "Browse SSH server…" flow (FR-18.5,
/// ENDPOINT_PATH_PICKER_PLAN.md's Phase 3): connects to a host resolved by
/// <see cref="SshConnectionResolver"/>, then exposes just the two
/// operations that flow needs — listing a directory's sub-folders and
/// creating a new one. Every call is a live network round-trip; there is
/// no caching here, that's the caller's (the lazy-loading TreeView's)
/// job.</summary>
public sealed class SftpDirectoryBrowser : IDisposable
{
    private readonly SftpClient _client;

    /// <summary>The directory the SFTP session lands in on connect (the
    /// remote user's home directory) — the natural root for the picker's
    /// tree, and the base a Mutagen endpoint's relative path is resolved
    /// against.</summary>
    public string HomeDirectory { get; }

    public SftpDirectoryBrowser(string host)
    {
        _client = new SftpClient(SshConnectionResolver.Resolve(host));
        _client.Connect();
        HomeDirectory = _client.WorkingDirectory;
    }

    public IReadOnlyList<string> ListSubDirectories(string path) =>
        _client.ListDirectory(path)
            .Where(entry => entry.IsDirectory && entry.Name != "." && entry.Name != "..")
            .Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public void Dispose()
    {
        if (_client.IsConnected)
            _client.Disconnect();
        _client.Dispose();
    }
}
