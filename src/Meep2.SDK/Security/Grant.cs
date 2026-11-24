using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// What a grant gives: one permission, to one credential, for a time. The part of a <see cref="Grant"/> its
/// authority signs.
/// </summary>
/// <remarks>
/// A grant names its credential by the credential's key, serial and authority, so it applies to that certificate
/// alone and not to one reissued for the same key.
/// </remarks>
public sealed record GrantTerms
{
    /// <summary>Terms giving <paramref name="permission"/> to <paramref name="credential"/>, with a new <see cref="Id"/>.</summary>
    public GrantTerms(Credential credential, PermissionName permission)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(permission);
        Id = Guid.CreateVersion7();
        CredentialKey = credential.KeyFingerprint;
        CredentialSerial = credential.Serial;
        CredentialAuthority = credential.AuthorityFingerprint;
        Permission = permission;
    }

    private GrantTerms(Guid id, string key, ulong serial, string authority, PermissionName permission)
    {
        Id = id;
        CredentialKey = key;
        CredentialSerial = serial;
        CredentialAuthority = authority;
        Permission = permission;
    }

    /// <summary>Identifies the grant, so it can be revoked alone (Q254).</summary>
    public Guid Id { get; init; }

    /// <summary>The credential's key's fingerprint.</summary>
    public string CredentialKey { get; }

    /// <summary>The credential's serial.</summary>
    public ulong CredentialSerial { get; }

    /// <summary>The fingerprint of the authority that issued the credential, since serials are only unique to one.</summary>
    public string CredentialAuthority { get; }

    /// <summary>The permission given.</summary>
    public PermissionName Permission { get; }

    /// <summary>When the grant starts, in UTC, or null for as soon as it's made.</summary>
    public DateTime? ValidAfter { get; init; }

    /// <summary>When the grant ends, in UTC, or null for when its credential does.</summary>
    public DateTime? ValidBefore { get; init; }

    /// <summary>Whether these terms name <paramref name="credential"/>.</summary>
    public bool AppliesTo(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.KeyFingerprint == CredentialKey
            && credential.Serial == CredentialSerial
            && credential.AuthorityFingerprint == CredentialAuthority;
    }

    /// <summary>
    /// The bytes an authority signs, in SSH's wire format: <c>meep-grant-v01</c>, the ID, the credential's key,
    /// serial and authority, the permission, and the two times.
    /// </summary>
    public byte[] ToSignedData() => new SshWireWriter()
        .Text(Grant.Format)
        .String(Id.ToByteArray(bigEndian: true))
        .Text(CredentialKey)
        .UInt64(CredentialSerial)
        .Text(CredentialAuthority)
        .Text(Permission.ToString())
        .UInt64(SshTime.FromAfter(ValidAfter))
        .UInt64(SshTime.FromBefore(ValidBefore))
        .ToArray();

    internal static GrantTerms Read(ref SshWireReader reader)
    {
        if (reader.ReadText() != Grant.Format)
            throw new FormatException($"it isn't a {Grant.Format}");
        var id = reader.ReadString();
        if (id.Length != 16)
            throw new FormatException("its ID isn't 16 bytes");
        return new GrantTerms(
            new Guid(id, bigEndian: true),
            reader.ReadText(),
            reader.ReadUInt64(),
            reader.ReadText(),
            PermissionName.Parse(reader.ReadText()))
        {
            ValidAfter = SshTime.After(reader.ReadUInt64()),
            ValidBefore = SshTime.Before(reader.ReadUInt64()),
        };
    }
}

/// <summary>
/// A permission given to a credential after it was issued, by <c>Authorise</c> (Q254): Meep's own signed record,
/// not an SSH certificate. The credential isn't reissued, and a grant can be revoked alone.
/// </summary>
/// <remarks>
/// <para>
/// The signature is an SSH signature (OpenSSH's PROTOCOL.sshsig) of <see cref="GrantTerms.ToSignedData"/> in the
/// <c>meep-grant</c> namespace, as <c>ssh-keygen -Y sign -n meep-grant</c> makes one, so the authority's key can stay
/// in ssh-agent. The authority's key is read from the signature.
/// </para>
/// <para>
/// A grant is only read here, never checked: whether its authority is trusted, its signature good, and it isn't
/// revoked, is for <c>Verify.Credential</c>.
/// </para>
/// </remarks>
public sealed record Grant : Message
{
    /// <summary>The name of the format, at the start of a grant's bytes and text.</summary>
    public const string Format = "meep-grant-v01";

    /// <summary>The SSH signature namespace grants are signed in.</summary>
    public const string Namespace = "meep-grant";

    private Grant(GrantTerms terms, byte[] signature)
    {
        Terms = terms;
        Signature = signature;
        var reader = new SshWireReader(signature);
        if (!Encoding.ASCII.GetString(reader.ReadRaw(6)).Equals("SSHSIG", StringComparison.Ordinal))
            throw new FormatException("its signature isn't an SSH signature");
        if (reader.ReadUInt32() != 1)
            throw new FormatException("its signature is of a version Meep doesn't know");
        AuthorityKey = reader.ReadString().ToArray();
        if (reader.ReadText() is var ns && ns != Namespace)
            throw new FormatException($"its signature is for {ns}, not {Namespace}");
        reader.ReadString(); // reserved
        reader.ReadText(); // the hash algorithm
        reader.ReadString(); // the signature itself
        reader.End();
        AuthorityFingerprint = Credential.Fingerprint(AuthorityKey.Span);
    }

    /// <summary>What it grants.</summary>
    public GrantTerms Terms { get; }

    /// <summary>The authority's signature of <see cref="GrantTerms.ToSignedData"/>, in PROTOCOL.sshsig's binary form.</summary>
    public ReadOnlyMemory<byte> Signature { get; }

    /// <summary>The key of the authority that signed it, read from its signature.</summary>
    public ReadOnlyMemory<byte> AuthorityKey { get; }

    /// <summary>The authority's key's SHA-256 fingerprint.</summary>
    public string AuthorityFingerprint { get; }

    /// <summary>
    /// Puts terms and a signature of them together. Throws <see cref="FormatException"/> if the signature isn't an
    /// SSH signature in the <c>meep-grant</c> namespace; whether it's a good one is checked when it's verified.
    /// </summary>
    public static Grant Signed(GrantTerms terms, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var copy = signature.ToArray();
        return Wrap(() => new Grant(terms, copy));
    }

    /// <summary>Reads a grant written as <see cref="ToText"/> writes it: <c>meep-grant-v01 base64</c>.</summary>
    public static Grant Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != Format)
            throw new FormatException($"A grant is written as: {Format} base64. This isn't one.");
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new FormatException("This grant's base64 isn't valid.");
        }
        return FromBlob(blob);
    }

    /// <summary>Reads a grant from its bytes: its terms' signed data, then its signature as a string.</summary>
    public static Grant FromBlob(ReadOnlySpan<byte> blob)
    {
        var copy = blob.ToArray();
        return Wrap(() =>
        {
            var reader = new SshWireReader(copy);
            var terms = GrantTerms.Read(ref reader);
            var signature = reader.ReadString().ToArray();
            reader.End();
            return new Grant(terms, signature);
        });
    }

    /// <summary>The grant's bytes: its terms' signed data, then its signature as a string.</summary>
    public byte[] ToBlob() => new SshWireWriter().Raw(Terms.ToSignedData()).String(Signature.Span).ToArray();

    /// <summary>The grant as text: <c>meep-grant-v01 base64</c>.</summary>
    public override string ToText() => $"{Format} {Convert.ToBase64String(ToBlob())}";

    private static Grant Wrap(Func<Grant> read)
    {
        try
        {
            return read();
        }
        catch (FormatException exception)
        {
            throw new FormatException($"This grant can't be read: {exception.Message.TrimEnd('.')}.", exception);
        }
    }
}
