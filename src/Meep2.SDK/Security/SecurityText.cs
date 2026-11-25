using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>What a piece of text is, as <see cref="SecurityText"/> tells it by its form.</summary>
public enum SecurityTextKind
{
    /// <summary>None of the others.</summary>
    None,

    /// <summary>An OpenSSH user certificate: a <see cref="Credential"/>.</summary>
    Credential,

    /// <summary>An X.509 certificate, PEM or DER in base64: a <see cref="Certificate"/>.</summary>
    Certificate,

    /// <summary>A permission's name, or a <c>#permission</c> line: a <see cref="Permission"/>.</summary>
    Permission,

    /// <summary>A grant, as <c>meep-grant-v02 base64</c> (or v01): a <see cref="Grant"/>.</summary>
    Grant,
}

/// <summary>
/// The one reader of credentials, certificates, permissions and grants written as text, which <c>Verify</c> and
/// <c>Parse</c> share, so the two can't disagree (Q293). It tells them apart by the text's form, cheaply, and reads
/// them in full only when asked.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An OpenSSH certificate, as a <c>-cert.pub</c> file has it or its base64 alone, is a credential.</item>
/// <item>PEM, or base64 that decodes to DER, is a certificate.</item>
/// <item><c>meep-grant-v02 base64</c>, or v01, is a grant.</item>
/// <item>A <c>#permission</c> line or a permission's name is a permission.</item>
/// </list>
/// An SSH certificate's base64 is recognised before a permission's name, since a long one could look like a name.
/// </remarks>
public static class SecurityText
{
    private const string CertificateSuffix = "-cert-v01@openssh.com";

    /// <summary>What <paramref name="text"/> is, by its form alone, or <see cref="SecurityTextKind.None"/>.</summary>
    public static SecurityTextKind KindOf(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        if (trimmed.IsEmpty)
            return SecurityTextKind.None;
        if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
            return SecurityTextKind.Certificate;
        if (trimmed.StartsWith(Permission.Directive, StringComparison.Ordinal))
            return SecurityTextKind.Permission;
        var first = FirstWord(trimmed);
        if (first.SequenceEqual(Grant.Format) || first.SequenceEqual(Grant.FormatV01))
            return SecurityTextKind.Grant;
        if (first.EndsWith(CertificateSuffix, StringComparison.Ordinal))
            return SecurityTextKind.Credential;
        if (Decoded(first) is { } bytes)
        {
            if (IsSshCertificate(bytes))
                return SecurityTextKind.Credential;
            if (bytes.Length > 0 && bytes[0] == 0x30)
                return SecurityTextKind.Certificate;
        }
        return PermissionName.TryParse(trimmed.ToString(), out _) ? SecurityTextKind.Permission : SecurityTextKind.None;
    }

    /// <summary>
    /// Reads <paramref name="text"/> as whatever it is: a <see cref="Credential"/>, <see cref="Certificate"/>,
    /// <see cref="Permission"/> or <see cref="Grant"/>. Throws <see cref="FormatException"/>, saying what it found,
    /// if it's none of them or can't be read as the one it looks like.
    /// </summary>
    public static Message Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return KindOf(text) switch
        {
            SecurityTextKind.None => throw new FormatException(WhatItIs(text)),
            var kind => Read(text, kind),
        };
    }

    /// <summary>
    /// Reads <paramref name="text"/> as <typeparamref name="T"/>, one of <see cref="Credential"/>,
    /// <see cref="Certificate"/>, <see cref="Permission"/> or <see cref="Grant"/>, whatever its form. Throws
    /// <see cref="FormatException"/>, saying why, if it can't be read as one.
    /// </summary>
    public static T Read<T>(string text)
        where T : Message
    {
        ArgumentNullException.ThrowIfNull(text);
        var kind = typeof(T) == typeof(Credential) ? SecurityTextKind.Credential
            : typeof(T) == typeof(Certificate) ? SecurityTextKind.Certificate
            : typeof(T) == typeof(Permission) ? SecurityTextKind.Permission
            : typeof(T) == typeof(Grant) ? SecurityTextKind.Grant
            : throw new ArgumentException($"{typeof(T).Name} isn't a credential, certificate, permission or grant.", nameof(T));
        return (T)Read(text, kind);
    }

    private static Message Read(string text, SecurityTextKind kind)
    {
        var trimmed = text.Trim();
        switch (kind)
        {
            case SecurityTextKind.Credential:
                return Credential.Parse(trimmed);
            case SecurityTextKind.Certificate:
                if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
                    return Certificate.FromPem(trimmed);
                return Decoded(trimmed) is { } der
                    ? Certificate.FromDer(der)
                    : throw new FormatException("A certificate is written as PEM, or as DER in base64. This is neither.");
            case SecurityTextKind.Grant:
                return Grant.Parse(trimmed);
            default:
                return trimmed.StartsWith(Permission.Directive, StringComparison.Ordinal)
                    ? Permission.ParseDirective(trimmed)
                    : new Permission(PermissionName.Parse(trimmed));
        }
    }

    // What text that's none of the four looks like, for saying so.
    private static string WhatItIs(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "There's nothing to read: the text is empty.";
        var first = FirstWord(trimmed).ToString();
        var shown = first.Length > 40 ? first[..40] + "…" : first;
        if (first.StartsWith("ssh-", StringComparison.Ordinal) || first.StartsWith("ecdsa-", StringComparison.Ordinal) || first.StartsWith("sk-", StringComparison.Ordinal))
            return $"This is an SSH public key ({shown}), not a certificate. A credential is a key signed by an authority, as ssh-keygen -s or meep trust issue makes one.";
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return "This is JSON, not a credential, certificate, permission or grant. Say which part to read with from=.";
        return $"This isn't a credential, certificate, permission or grant: it starts \"{shown}\".";
    }

    private static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> text)
    {
        var space = text.IndexOfAny(' ', '\t');
        return space < 0 ? text : text[..space];
    }

    private static byte[]? Decoded(ReadOnlySpan<char> base64)
    {
        if (base64.Length == 0 || base64.Length % 4 != 0)
            return null;
        var bytes = new byte[base64.Length / 4 * 3];
        return Convert.TryFromBase64Chars(base64, bytes, out var written) ? bytes[..written] : null;
    }

    // An SSH certificate starts with its type's name, as a length and the name.
    private static bool IsSshCertificate(byte[] bytes)
    {
        if (bytes.Length < 4)
            return false;
        var length = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        return length is > 0 and < 64 && bytes.Length >= 4 + length
            && Encoding.ASCII.GetString(bytes, 4, length).EndsWith(CertificateSuffix, StringComparison.Ordinal);
    }
}
