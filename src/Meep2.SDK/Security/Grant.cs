using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// What a grant gives: one permission, to one credential, for a time. The part of a <see cref="Grant"/> its
/// authority signs.
/// </summary>
/// <remarks>
/// <para>
/// A grant names its credential by the credential's key, serial and authority, so it applies to that certificate
/// alone and not to one reissued for the same key.
/// </para>
/// <para>
/// Since <c>meep-grant-v02</c>, it names the credentials that authorised it the same way (<see cref="AuthorisedBy"/>),
/// so revoking one of them revokes the grant too (Q301). Terms read from a <c>meep-grant-v01</c> grant have none, and
/// are signed and written as v01 still, so its signature stays good.
/// </para>
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

    private GrantTerms(string format, Guid id, string key, ulong serial, string authority, PermissionName permission)
    {
        Format = format;
        Id = id;
        CredentialKey = key;
        CredentialSerial = serial;
        CredentialAuthority = authority;
        Permission = permission;
    }

    /// <summary>
    /// The format its signed data is in: <c>meep-grant-v02</c> for new terms, or <c>meep-grant-v01</c> for terms read
    /// from an older grant.
    /// </summary>
    public string Format { get; } = Grant.Format;

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

    /// <summary>
    /// The credentials that authorised the grant, one for each line of the authorising message's family; revoking one
    /// revokes the grant (Q301). Empty for a grant made without one, such as by a test or a v01 grant. Only
    /// <c>meep-grant-v02</c> terms can have any.
    /// </summary>
    public IReadOnlyList<CredentialName> AuthorisedBy
    {
        get;
        init => field = value is { Count: > 0 } && Format != Grant.Format
            ? throw new InvalidOperationException($"{Format} terms can't name who authorised them.")
            : value ?? [];
    } = [];

    /// <summary>Whether two terms are the same, field for field, with the same authorisers in the same order.</summary>
    public bool Equals(GrantTerms? other) =>
        other is not null
        && Format == other.Format
        && Id == other.Id
        && CredentialKey == other.CredentialKey
        && CredentialSerial == other.CredentialSerial
        && CredentialAuthority == other.CredentialAuthority
        && Permission == other.Permission
        && ValidAfter == other.ValidAfter
        && ValidBefore == other.ValidBefore
        && AuthorisedBy.SequenceEqual(other.AuthorisedBy);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, CredentialKey, CredentialSerial, Permission, ValidBefore, AuthorisedBy.Count);

    /// <summary>Whether these terms name <paramref name="credential"/>.</summary>
    public bool AppliesTo(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.KeyFingerprint == CredentialKey
            && credential.Serial == CredentialSerial
            && credential.AuthorityFingerprint == CredentialAuthority;
    }

    /// <summary>
    /// The bytes an authority signs, in SSH's wire format: the format's name, the ID, the credential's key, serial and
    /// authority, the permission, and the two times; then, for <c>meep-grant-v02</c>, how many credentials authorised
    /// it, and each one's key, serial and authority.
    /// </summary>
    public byte[] ToSignedData()
    {
        var writer = new SshWireWriter()
            .Text(Format)
            .String(Id.ToByteArray(bigEndian: true))
            .Text(CredentialKey)
            .UInt64(CredentialSerial)
            .Text(CredentialAuthority)
            .Text(Permission.ToString())
            .UInt64(SshTime.FromAfter(ValidAfter))
            .UInt64(SshTime.FromBefore(ValidBefore));
        if (Format == Grant.Format)
        {
            writer.UInt32((uint)AuthorisedBy.Count);
            foreach (var authoriser in AuthorisedBy)
                writer.Text(authoriser.Key).UInt64(authoriser.Serial).Text(authoriser.Authority);
        }
        return writer.ToArray();
    }

    internal static GrantTerms Read(ref SshWireReader reader)
    {
        var format = reader.ReadText();
        if (format is not (Grant.Format or Grant.FormatV01))
            throw new FormatException($"it isn't a {Grant.Format} or {Grant.FormatV01}");
        var id = reader.ReadString();
        if (id.Length != 16)
            throw new FormatException("its ID isn't 16 bytes");
        var terms = new GrantTerms(
            format,
            new Guid(id, bigEndian: true),
            reader.ReadText(),
            reader.ReadUInt64(),
            reader.ReadText(),
            PermissionName.Parse(reader.ReadText()))
        {
            ValidAfter = SshTime.After(reader.ReadUInt64()),
            ValidBefore = SshTime.Before(reader.ReadUInt64()),
        };
        if (format == Grant.FormatV01)
            return terms;
        var count = reader.ReadUInt32();
        if (count > MostAuthorisers)
            throw new FormatException($"it names {count} authorisers, more than {MostAuthorisers}");
        var authorisers = new CredentialName[count];
        for (var i = 0; i < authorisers.Length; i++)
            authorisers[i] = new CredentialName(reader.ReadText(), reader.ReadUInt64(), reader.ReadText());
        return terms with { AuthorisedBy = authorisers };
    }

    // More than a grant could sensibly have, so a damaged one can't ask for a huge array.
    private const int MostAuthorisers = 1_000;
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
    /// <summary>The name of the format new grants are in, at the start of a grant's bytes and text.</summary>
    public const string Format = "meep-grant-v02";

    /// <summary>The name of the first format, which doesn't name who authorised a grant, still read.</summary>
    public const string FormatV01 = "meep-grant-v01";

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

    /// <summary>Reads a grant written as <see cref="ToText"/> writes it: <c>meep-grant-v02 base64</c>, or v01.</summary>
    public static Grant Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not (Format or FormatV01))
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

    /// <summary>The grant as text: <c>meep-grant-v02 base64</c>, or v01 for a grant read as one.</summary>
    public override string ToText() => $"{Terms.Format} {Convert.ToBase64String(ToBlob())}";

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

/// <summary>
/// A credential, named by its key's fingerprint, its serial and its authority's fingerprint, as a grant names who
/// authorised it. That's the certificate alone, not another issued for the same key.
/// </summary>
/// <param name="Key">The credential's key's SHA-256 fingerprint.</param>
/// <param name="Serial">Its serial, 0 for none.</param>
/// <param name="Authority">The fingerprint of the authority that issued it.</param>
public sealed record CredentialName(string Key, ulong Serial, string Authority)
{
    /// <summary>Names <paramref name="credential"/>.</summary>
    public static CredentialName Of(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new(credential.KeyFingerprint, credential.Serial, credential.AuthorityFingerprint);
    }

    /// <summary>Whether it names <paramref name="credential"/>.</summary>
    public bool Names(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.KeyFingerprint == Key && credential.Serial == Serial && credential.AuthorityFingerprint == Authority;
    }
}
