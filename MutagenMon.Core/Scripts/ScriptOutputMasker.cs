namespace MutagenMon.Core.Scripts;

/// <summary>Replaces the secret values that came from macros by
/// <see cref="Replacement"/> in the output of a script (FR-55.8), before the
/// text reaches the output pane or any log. The match is exact and
/// case-sensitive, best effort: it covers <c>set -x</c>, <c>echo</c> and
/// error messages, not a transformed secret (base64, partial value). The
/// secrets are replaced one after the other, so two secrets that partly
/// overlap in the output (<c>abc</c> and <c>cde</c> in <c>abcde</c>) can leave
/// a fragment of the second one.</summary>
public sealed class ScriptOutputMasker
{
    public const string Replacement = "***";

    private readonly string[] _secrets;

    public ScriptOutputMasker(IEnumerable<string> secrets)
    {
        // Longest first: a secret that contains another one must be masked as a whole,
        // not partly replaced by the shorter one. Empty values would match everywhere.
        _secrets = secrets
            .Where(secret_ => secret_.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret_ => secret_.Length)
            .ToArray();
    }

    /// <summary>The line with every secret replaced; the same instance when
    /// there is nothing to mask.</summary>
    public string Mask(string line)
    {
        foreach (var secret in _secrets)
            line = line.Replace(secret, Replacement, StringComparison.Ordinal);
        return line;
    }
}
