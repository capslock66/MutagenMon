using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace MutagenMon.Core.Scripts;

/// <summary>Reads, on the Windows workstation, the values behind the
/// <c>env</c>, <c>reg</c> and <c>cred</c> macros (FR-55.2, FR-55.9, FR-55.10).
/// Each method has the shape of the matching delegate of
/// <see cref="ScriptMacroSources"/>, which Core receives instead of reading
/// anything itself (NFR-11). Every value is read at call time, so a change
/// made after MutagenMon started is seen without a restart. A missing,
/// unreadable or unsupported value is <c>null</c>, never an exception: the
/// expander reports it as an unresolved macro (FR-55.6). Off Windows the
/// registry and the Credential Manager do not exist, so only the process
/// environment answers. The Windows parts cannot be unit-tested on Linux
/// (manual scenarios UT-55.11 to UT-55.13); the value formatting and the
/// expansion of <c>%NAME%</c> references are pure and are unit-tested.</summary>
public static class WindowsMacroValueReader
{
    private const string UserEnvironmentKey = "Environment";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    // A %OTHER% reference read from the registry may itself contain references: stop after this depth
    // as a safety net (a reference to a variable that is already being expanded is not followed at all).
    private const int MaxExpansionDepth = 5;

    /// <summary>An environment variable as stored, before the expansion of
    /// its <c>%NAME%</c> references: the text, and whether it is a
    /// <c>REG_EXPAND_SZ</c> value whose references must be expanded.</summary>
    internal readonly record struct RawEnvironmentValue(string Text, bool IsExpandable);

    /// <summary>The environment variable <paramref name="name"/> of the
    /// Windows workstation (FR-55.2): the user value of
    /// <c>HKCU\Environment</c>, else the machine value, as Windows does, else
    /// the process environment of MutagenMon (variables injected when it was
    /// launched). <c>REG_EXPAND_SZ</c> values are expanded.</summary>
    public static string? ReadEnvironmentVariable(string name) =>
        ResolveEnvironmentVariable(name, ReadRawEnvironmentVariable);

    /// <summary>The data of the value <paramref name="valueName"/> of the
    /// registry key <paramref name="key"/> (FR-55.9). The key starts with
    /// <c>HKCU</c> or <c>HKLM</c>, as normalised by
    /// <see cref="ScriptMacroExpander"/>; the 64-bit registry view is read.</summary>
    public static string? ReadRegistryValue(string key, string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return ReadRegistryValueOnWindows(key, valueName);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Access denied, key being deleted, bad key text: the value cannot be read.
            return null;
        }
    }

    /// <summary>The password of the generic credential <paramref name="target"/>
    /// of the Windows Credential Manager of the current user (FR-55.10). The
    /// user name stored with it is ignored.</summary>
    public static string? ReadCredentialPassword(string target)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return ReadCredentialPasswordOnWindows(target);
        }
        catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Text of a registry value (FR-55.9): <c>REG_SZ</c> verbatim,
    /// <c>REG_EXPAND_SZ</c> expanded with <paramref name="expand"/>,
    /// <c>REG_DWORD</c> and <c>REG_QWORD</c> as unsigned decimal text. Any
    /// other type (<c>REG_BINARY</c>, <c>REG_MULTI_SZ</c>...), a missing value
    /// (<c>null</c>) or a value whose .NET type does not match its kind gives
    /// <c>null</c>.</summary>
    // The enum RegistryValueKind is annotated Windows-only by the framework, but this switch is pure
    // and is unit-tested on Linux: the attribute only satisfies the platform analyzer (CA1416).
    [SupportedOSPlatform("windows")]
    internal static string? FormatRegistryValue(object? value, RegistryValueKind kind, Func<string, string> expand) => (kind, value) switch
    {
        (RegistryValueKind.String, string text) => text,
        (RegistryValueKind.ExpandString, string text) => expand(text),
        // The registry stores the DWORD/QWORD bits; .NET hands them back signed.
        (RegistryValueKind.DWord, int number) => unchecked((uint)number).ToString(CultureInfo.InvariantCulture),
        (RegistryValueKind.QWord, long number) => unchecked((ulong)number).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The password held by the blob of a credential (FR-55.10): UTF-16
    /// text, as written by <c>cmdkey</c> and the Windows Credential Manager. An
    /// odd size cannot be UTF-16 (the blob was written in another encoding) and
    /// is unreadable; trailing null characters (some tools store the
    /// terminator with the text) are not part of the password. An empty
    /// password is <c>null</c>.</summary>
    internal static string? DecodeCredentialBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 2 || blob.Length % 2 != 0)
            return null;

        // Strict decoding: an invalid UTF-16 sequence (a lone surrogate) would otherwise become U+FFFD
        // and give a wrong password without any error, instead of an unreadable one.
        string password;
        try
        {
            password = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(blob).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return password.Length == 0 ? null : password;
    }

    /// <summary>Replaces each <c>%NAME%</c> of <paramref name="text"/> by
    /// <paramref name="lookup"/>(NAME). A <c>%</c> that does not open a known
    /// reference stays as written, and the next <c>%</c> may open a reference
    /// of its own: <c>50% of %HOME%</c> expands <c>%HOME%</c>. This is the rule
    /// of the managed implementation of
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/>; it has not
    /// been compared with the Windows <c>ExpandEnvironmentStrings</c> for two
    /// references stuck together (open question in findings.md).</summary>
    internal static string ExpandReferences(string text, Func<string, string?> lookup)
    {
        var result = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('%', position);
            if (open < 0)
                break;
            var close = text.IndexOf('%', open + 1);
            if (close < 0)
                break;

            var name = text[(open + 1)..close];
            var value = name.Length > 0 && !name.Contains('\r') && !name.Contains('\n') ? lookup(name) : null;
            if (value is null)
            {
                // Not a known reference: keep the text up to the closing percent sign, which may open the next one.
                result.Append(text, position, close - position);
                position = close;
                continue;
            }

            result.Append(text, position, open - position).Append(value);
            position = close + 1;
        }

        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    /// <summary>The value of an environment variable with its <c>%NAME%</c>
    /// references expanded, each read through <paramref name="rawLookup"/>.
    /// A reference to a variable that is already being expanded (a
    /// <c>Path</c> defined as <c>%Path%;C:\tools</c>, or two variables
    /// referring to each other) is not followed and stays as written, and
    /// the chain stops after <see cref="MaxExpansionDepth"/> levels.</summary>
    internal static string? ResolveEnvironmentVariable(string name, Func<string, RawEnvironmentValue?> rawLookup) =>
        ResolveEnvironmentVariable(name, rawLookup, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);

    private static string? ResolveEnvironmentVariable(
        string name, Func<string, RawEnvironmentValue?> rawLookup, HashSet<string> beingExpanded, int depth)
    {
        if (rawLookup(name) is not { } raw)
            return null;
        if (!raw.IsExpandable || depth >= MaxExpansionDepth)
            return raw.Text;

        beingExpanded.Add(name);
        try
        {
            return ExpandReferences(
                raw.Text,
                reference_ => beingExpanded.Contains(reference_) ? null : ResolveEnvironmentVariable(reference_, rawLookup, beingExpanded, depth + 1));
        }
        finally
        {
            beingExpanded.Remove(name);
        }
    }

    private static RawEnvironmentValue? ReadRawEnvironmentVariable(string name)
    {
        // The user value overrides the machine value, as Windows does; each hive is read on its own,
        // so that a hive that cannot be read does not hide the other one.
        var fromRegistry =
            TryReadRawEnvironmentValue(isUserHive: true, UserEnvironmentKey, name)
            ?? TryReadRawEnvironmentValue(isUserHive: false, MachineEnvironmentKey, name);
        if (fromRegistry is not null)
            return fromRegistry;

        // A value of the process environment is already final: its references are not expanded again.
        return Environment.GetEnvironmentVariable(name) is { } fromProcess ? new RawEnvironmentValue(fromProcess, false) : null;
    }

    /// <summary>The value read from one registry hive, or <c>null</c> off
    /// Windows, when it is missing or of an unsupported type, or when the
    /// registry cannot be read.</summary>
    private static RawEnvironmentValue? TryReadRawEnvironmentValue(bool isUserHive, string subKey, string name)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return ReadRawEnvironmentValueOnWindows(isUserHive ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, subKey, name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static RawEnvironmentValue? ReadRawEnvironmentValueOnWindows(RegistryHive hive, string subKey, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey);
        if (key is null)
            return null;

        // DoNotExpandEnvironmentNames: the raw text is needed so that the references are expanded here, from the registry too.
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;

        var kind = key.GetValueKind(name);
        return FormatRegistryValue(value, kind, text_ => text_) is { } text
            ? new RawEnvironmentValue(text, kind == RegistryValueKind.ExpandString)
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryValueOnWindows(string key, string valueName)
    {
        var firstBackslash = key.IndexOf('\\');
        if (firstBackslash <= 0)
            return null;

        var hive = key[..firstBackslash] switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => (RegistryHive?)null,
        };
        if (hive is null)
            return null;

        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        using var subKey = root.OpenSubKey(key[(firstBackslash + 1)..]);
        if (subKey is null)
            return null;

        var value = subKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;

        // A %NAME% reference of a REG_EXPAND_SZ value is expanded like an environment variable (FR-55.2).
        return FormatRegistryValue(
            value, subKey.GetValueKind(valueName),
            text_ => ExpandReferences(text_, name_ => ResolveEnvironmentVariable(name_, ReadRawEnvironmentVariable)));
    }

    // ---- Windows Credential Manager (advapi32) ----

    private const uint CredTypeGeneric = 1;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    // Native CREDENTIALW layout. Only CredentialBlobSize and CredentialBlob are used here.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCredentialPasswordOnWindows(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPointer))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                return DecodeCredentialBlob(blob);
            }
            finally
            {
                // The copy holds the password in clear: do not leave it in memory until the next collection.
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }
}
