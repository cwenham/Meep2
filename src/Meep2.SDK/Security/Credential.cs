using System.Security.Cryptography;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// A credential: an OpenSSH user certificate (Q251), as <c>ssh-keygen -s</c> issues it. It names its subject (key ID
/// and principals), holds the subject's public key, is in date between two times, has a serial, and is signed by an
/// authority's key (the certificate authority). Its permissions are extensions named <c>name@meep</c>, with a scope,
/// if any, as the extension's value.
/// </summary>
/// <remarks>
/// <para>
/// A credential is only read here, never checked: whether its authority is trusted, its signature good, it's in date
/// and not revoked, and whether whoever presented it holds its key, is for <c>Verify.Credential</c>, which emits a
/// <see cref="VerifiedMessage"/>. Its fields are read from the same bytes the signature covers, and can't be changed
/// afterwards, so what's checked is what's shown.
/// </para>
/// <para>
/// Issue one with permissions as extensions:
/// <code>ssh-keygen -s ca -I alice -n alice -V +52w -O extension:ReadNotes@meep -O extension:meep.files.write@meep=~/notes/** alice.pub</code>
/// </para>
/// </remarks>
public sealed record Credential : Message
{
    /// <summary>The suffix that marks an extension as one of Meep's.</summary>
    public const string ExtensionSuffix = "@meep";

    /// <summary>
    /// The extension by which the issuer allows the credential to be used as a bearer credential, without proof that
    /// whoever presents it holds its key (Q253).
    /// </summary>
    public const string PermitBearerExtension = "permit-bearer@meep";

    // The certificate types Meep reads, and how many length-prefixed fields their public key has.
    private static readonly Dictionary<string, KeyLayout> s_types = new(StringComparer.Ordinal)
    {
        ["ssh-ed25519-cert-v01@openssh.com"] = new("ssh-ed25519", 1),
        ["ecdsa-sha2-nistp256-cert-v01@openssh.com"] = new("ecdsa-sha2-nistp256", 2),
        ["ecdsa-sha2-nistp384-cert-v01@openssh.com"] = new("ecdsa-sha2-nistp384", 2),
        ["ecdsa-sha2-nistp521-cert-v01@openssh.com"] = new("ecdsa-sha2-nistp521", 2),
        ["ssh-rsa-cert-v01@openssh.com"] = new("ssh-rsa", 2),
        ["sk-ssh-ed25519-cert-v01@openssh.com"] = new("sk-ssh-ed25519@openssh.com", 2),
        ["sk-ecdsa-sha2-nistp256-cert-v01@openssh.com"] = new("sk-ecdsa-sha2-nistp256@openssh.com", 3),
    };

    // A key type, and how many length-prefixed fields its public key has in a certificate.
    private sealed record KeyLayout(string KeyType, int Fields);

    private Credential(byte[] blob)
    {
        Blob = blob;
        var reader = new SshWireReader(blob);
        CertificateType = reader.ReadText();
        if (!s_types.TryGetValue(CertificateType, out var type))
            throw new FormatException($"{CertificateType} isn't a kind of SSH certificate Meep reads");
        KeyType = type.KeyType;
        reader.ReadString(); // the nonce, which only makes the signed bytes unpredictable
        var keyStart = reader.Position;
        for (var i = 0; i < type.Fields; i++)
            reader.ReadString();
        Key = new SshWireWriter().Text(KeyType).Raw(reader.Since(keyStart)).ToArray();
        Serial = reader.ReadUInt64();
        Kind = reader.ReadUInt32() switch
        {
            1 => SshCertificateKind.User,
            2 => SshCertificateKind.Host,
            var other => throw new FormatException($"its type, {other}, is neither a user's nor a host's"),
        };
        KeyId = reader.ReadText();
        Principals = ReadPrincipals(reader.ReadString());
        ValidAfter = SshTime.After(reader.ReadUInt64());
        ValidBefore = SshTime.Before(reader.ReadUInt64());
        CriticalOptions = ReadOptions(reader.ReadString(), "critical option");
        Extensions = ReadOptions(reader.ReadString(), "extension");
        reader.ReadString(); // reserved
        AuthorityKey = reader.ReadString().ToArray();
        SignedData = blob.AsMemory(0, reader.Position);
        Signature = reader.ReadString().ToArray();
        reader.End();

        KeyFingerprint = Fingerprint(Key.Span);
        AuthorityFingerprint = Fingerprint(AuthorityKey.Span);
        Permissions = PermissionsIn(Extensions);
        PermitsBearer = Extensions.ContainsKey(PermitBearerExtension);
    }

    /// <summary>The certificate's type, such as <c>ssh-ed25519-cert-v01@openssh.com</c>.</summary>
    public string CertificateType { get; }

    /// <summary>The subject's key type, such as <c>ssh-ed25519</c>.</summary>
    public string KeyType { get; }

    /// <summary>The subject's public key in SSH's wire format, as the base64 in a <c>.pub</c> file holds it.</summary>
    public ReadOnlyMemory<byte> Key { get; }

    /// <summary>The subject's key's SHA-256 fingerprint, as <c>ssh-keygen -l</c> shows it.</summary>
    public string KeyFingerprint { get; }

    /// <summary>The serial the authority gave it, by which it can be revoked. Zero if the authority gave none.</summary>
    public ulong Serial { get; }

    /// <summary>Whether it's a user's certificate or a host's. Credentials are users'.</summary>
    public SshCertificateKind Kind { get; }

    /// <summary>The key ID the authority gave it (<c>ssh-keygen -I</c>), which names the subject in logs.</summary>
    public string KeyId { get; }

    /// <summary>The names it's valid for (<c>ssh-keygen -n</c>). Empty means any.</summary>
    public IReadOnlyList<string> Principals { get; }

    /// <summary>When it starts to be valid, in UTC, or null for always.</summary>
    public DateTime? ValidAfter { get; }

    /// <summary>When it stops being valid, in UTC, or null for never.</summary>
    public DateTime? ValidBefore { get; }

    /// <summary>
    /// Its critical options, by name, with their values (empty for a flag). A credential with one Meep doesn't
    /// understand must be refused.
    /// </summary>
    public IReadOnlyDictionary<string, string> CriticalOptions { get; }

    /// <summary>Its extensions, by name, with their values (empty for a flag).</summary>
    public IReadOnlyDictionary<string, string> Extensions { get; }

    /// <summary>The permissions it carries: its extensions named <c>name@meep</c>, other than Meep's own flags.</summary>
    public IReadOnlyList<PermissionName> Permissions { get; }

    /// <summary>Whether its issuer allows it to be used without proof of holding its key (<see cref="PermitBearerExtension"/>).</summary>
    public bool PermitsBearer { get; }

    /// <summary>The key of the authority that signed it, in SSH's wire format.</summary>
    public ReadOnlyMemory<byte> AuthorityKey { get; }

    /// <summary>The authority's key's SHA-256 fingerprint.</summary>
    public string AuthorityFingerprint { get; }

    /// <summary>The bytes the authority signed: everything up to the signature.</summary>
    public ReadOnlyMemory<byte> SignedData { get; }

    /// <summary>The authority's signature, in SSH's wire format: the algorithm's name, then the signature.</summary>
    public ReadOnlyMemory<byte> Signature { get; }

    /// <summary>The whole certificate in SSH's wire format.</summary>
    public ReadOnlyMemory<byte> Blob { get; }

    /// <summary>Whether it's in date at <paramref name="when"/>.</summary>
    public bool InDateAt(DateTime when)
    {
        when = when.ToUniversalTime();
        return (ValidAfter is null || when >= ValidAfter) && (ValidBefore is null || when < ValidBefore);
    }

    /// <summary>
    /// Reads a credential as an <c>-cert.pub</c> file has it: <c>type base64 [comment]</c>, or the base64 alone.
    /// Throws <see cref="FormatException"/>, saying why, if it can't.
    /// </summary>
    public static Credential Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new FormatException("A credential is an SSH certificate, written as: type base64. This is empty.");
        var base64 = parts.Length == 1 ? parts[0] : parts[1];
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new FormatException("A credential is an SSH certificate, written as: type base64. Its base64 isn't valid.");
        }
        var credential = FromBlob(blob);
        if (parts.Length > 1 && parts[0] != credential.CertificateType)
            throw new FormatException($"The credential says it's {parts[0]}, but it's {credential.CertificateType}.");
        return credential;
    }

    /// <summary>Reads a credential from SSH's wire format. Throws <see cref="FormatException"/>, saying why, if it can't.</summary>
    public static Credential FromBlob(ReadOnlySpan<byte> blob)
    {
        try
        {
            return new Credential(blob.ToArray());
        }
        catch (FormatException exception)
        {
            throw new FormatException($"This credential can't be read: {exception.Message}.", exception);
        }
    }

    /// <summary>The credential as a <c>-cert.pub</c> file has it, without a comment: <c>type base64</c>.</summary>
    public override string ToText() => $"{CertificateType} {Convert.ToBase64String(Blob.Span)}";

    internal static string Fingerprint(ReadOnlySpan<byte> key) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    private static List<string> ReadPrincipals(ReadOnlySpan<byte> packed)
    {
        var reader = new SshWireReader(packed);
        var principals = new List<string>();
        while (!reader.AtEnd)
            principals.Add(reader.ReadText());
        return principals;
    }

    // Name and value pairs. A value is either empty (a flag) or a string inside the data string.
    private static Dictionary<string, string> ReadOptions(ReadOnlySpan<byte> packed, string what)
    {
        var reader = new SshWireReader(packed);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!reader.AtEnd)
        {
            var name = reader.ReadText();
            var data = new SshWireReader(reader.ReadString());
            var value = data.AtEnd ? "" : data.ReadText();
            data.End();
            if (!options.TryAdd(name, value))
                throw new FormatException($"it has the {what} {name} twice");
        }
        return options;
    }

    private static List<PermissionName> PermissionsIn(IReadOnlyDictionary<string, string> extensions)
    {
        var permissions = new List<PermissionName>();
        foreach (var (extension, value) in extensions)
        {
            if (!extension.EndsWith(ExtensionSuffix, StringComparison.Ordinal) || extension == PermitBearerExtension)
                continue;
            var name = extension[..^ExtensionSuffix.Length];
            try
            {
                permissions.Add(new PermissionName(name, value.Length == 0 ? null : PermissionScope.Parse(value)));
            }
            catch (FormatException exception)
            {
                throw new FormatException($"its extension {extension} isn't a permission Meep can read ({exception.Message.TrimEnd('.')})", exception);
            }
        }
        return permissions;
    }
}

/// <summary>Whether an SSH certificate is a user's or a host's.</summary>
public enum SshCertificateKind
{
    /// <summary>A user's certificate, which a credential is.</summary>
    User = 1,

    /// <summary>A host's certificate, which identifies a server.</summary>
    Host = 2,
}
