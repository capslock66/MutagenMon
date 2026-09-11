using System.Text;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Reads/writes mutagen's own global configuration file
/// (<c>%USERPROFILE%\.mutagen.yml</c>,
/// requirements/08-mutagen-config-editor-requirements.md) — distinct from
/// MutagenMon's own <see cref="ConfigLoader"/>-loaded
/// <c>config_mutagenmon.json</c>, and from mutagen's
/// <c>%USERPROFILE%\.mutagen</c> data directory. This is the app's first
/// feature that writes to a file rather than only reading config or
/// shelling out to the <c>mutagen</c> CLI.
/// </summary>
public static class MutagenConfigFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Fixed path this feature always targets (FR-30.2) — not
    /// user-configurable.</summary>
    public static string ResolvePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mutagen.yml");

    /// <summary><see cref="Content"/> is empty and <see cref="FileExisted"/>
    /// is false when the file wasn't there yet (FR-30.4). <see
    /// cref="Encoding"/> is the file's own detected encoding (FR-30.3), or
    /// UTF-8 without BOM for a not-yet-existing file (FR-32.2).</summary>
    public readonly record struct LoadResult(string Content, Encoding Encoding, bool FileExisted);

    /// <summary><paramref name="path"/> is a parameter (rather than always
    /// <see cref="ResolvePath"/> internally) so tests can round-trip through
    /// a temp file instead of the real, shared
    /// <c>%USERPROFILE%\.mutagen.yml</c>.</summary>
    public static LoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new LoadResult("", Utf8NoBom, FileExisted: false);

        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new LoadResult(content, encoding, FileExisted: true);
    }

    /// <summary>Overwrites (or creates) <paramref name="path"/> with
    /// <paramref name="content"/>, using <paramref name="encoding"/> —
    /// normally the one <see cref="Load"/> detected, so a re-save preserves
    /// the file's original encoding (including BOM presence/absence) rather
    /// than silently normalizing it (FR-32.2). No locking or
    /// external-change detection: last write wins (FR-32.3).</summary>
    public static void Save(string path, string content, Encoding encoding) => File.WriteAllText(path, content, encoding);

    /// <summary>Detects the encoding from a leading byte-order mark, falling
    /// back to UTF-8 without BOM when none is present. Deliberately not
    /// using <c>StreamReader</c>'s own BOM detection here: its
    /// <c>CurrentEncoding</c> can't reliably distinguish "no BOM was found,
    /// fell back to the caller-supplied default" from "the BOM found happens
    /// to match that same default", which matters here because
    /// <see cref="Encoding.UTF8"/>'s preamble (used for the BOM case) is
    /// non-empty while the no-BOM fallback must have none, and getting that
    /// wrong would add or drop a BOM the original file didn't have.</summary>
    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return Encoding.UTF8;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Utf8NoBom;
    }
}
