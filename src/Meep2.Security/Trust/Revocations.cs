using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meep2.SDK.Security;
using Org.BouncyCastle.X509;

namespace Meep2.Security;

/// <summary>What one line of the trust store's <c>revoked</c> file revokes.</summary>
public enum RevocationKind
{
    /// <summary>A credential, by its authority's key's fingerprint and its serial.</summary>
    Serial,

    /// <summary>A key, by its fingerprint: every credential for it, and everything it signed as an authority.</summary>
    Key,

    /// <summary>A grant, by its ID.</summary>
    Grant,

    /// <summary>An X.509 certificate, by its SHA-256 hash in hexadecimal.</summary>
    Certificate,
}

/// <summary>One line of the trust store's <c>revoked</c> file: <c>serial SHA256:… 42</c>, <c>key SHA256:…</c>,
/// <c>grant 0193…</c> or <c>certificate 3F9A…</c>.</summary>
/// <param name="Kind">What it revokes.</param>
/// <param name="Value">The fingerprint, ID or hash it names.</param>
/// <param name="Serial">The serial, for <see cref="RevocationKind.Serial"/>.</param>
public sealed record Revocation(RevocationKind Kind, string Value, ulong Serial = 0)
{
    /// <summary>Revokes a credential by its authority and serial. Throws <see cref="FormatException"/> for serial 0, which means none.</summary>
    public static Revocation Of(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.Serial == 0
            ? throw new FormatException($"{credential.KeyId}'s credential has no serial, so it can only be revoked by its key.")
            : new Revocation(RevocationKind.Serial, credential.AuthorityFingerprint, credential.Serial);
    }

    /// <summary>Reads a line as <see cref="ToString"/> writes it. Throws <see cref="FormatException"/> if it can't.</summary>
    public static Revocation Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words switch
        {
            ["serial", var authority, var serial] when IsFingerprint(authority) && ulong.TryParse(serial, out var number) && number > 0 =>
                new Revocation(RevocationKind.Serial, authority, number),
            ["key", var key] when IsFingerprint(key) => new Revocation(RevocationKind.Key, key),
            ["grant", var id] when Guid.TryParse(id, out var guid) => new Revocation(RevocationKind.Grant, guid.ToString()),
            ["certificate", var hash] when hash.Length == 64 && hash.All(char.IsAsciiHexDigit) =>
                new Revocation(RevocationKind.Certificate, hash.ToUpperInvariant()),
            _ => throw new FormatException(
                "A revocation is one of: serial <authority's fingerprint> <serial>, key <fingerprint>, grant <ID>, or certificate <SHA-256 in hex>."),
        };
    }

    /// <summary>The line, as the <c>revoked</c> file has it.</summary>
    public override string ToString() => Kind switch
    {
        RevocationKind.Serial => $"serial {Value} {Serial}",
        RevocationKind.Key => $"key {Value}",
        RevocationKind.Grant => $"grant {Value}",
        _ => $"certificate {Value}",
    };

    private static bool IsFingerprint(string text) => text.StartsWith("SHA256:", StringComparison.Ordinal) && text.Length > 7;
}

/// <summary>
/// A revocation, and what it revokes in words, for the comment above its line in <c>revoked</c> and for saying so. Made
/// for each kind of thing that can be revoked, as <c>meep trust revoke</c> and the <c>Revoke</c> verb do.
/// </summary>
/// <param name="Revocation">The line.</param>
/// <param name="What">What it revokes, in words.</param>
public sealed record Revoking(Revocation Revocation, string What)
{
    /// <summary>
    /// When what it revokes expires, in UTC, after which the line isn't needed and <c>meep trust tidy</c> may take it
    /// out (Q305), or null if it lasts for ever or that isn't known.
    /// </summary>
    public DateTime? Until { get; init; }

    /// <summary>A credential, by its authority and serial. Throws <see cref="FormatException"/> for one with no serial.</summary>
    public static Revoking Credential(Credential credential) =>
        new(Revocation.Of(credential), $"{credential.KeyId}'s credential, serial {credential.Serial} from {credential.AuthorityFingerprint}")
        {
            Until = credential.ValidBefore,
        };

    /// <summary>A credential's key: every credential for it, and everything it signed as an authority.</summary>
    public static Revoking KeyOf(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new(new Revocation(RevocationKind.Key, credential.KeyFingerprint), $"{credential.KeyId}'s key {credential.KeyFingerprint}, and every credential for it");
    }

    /// <summary>A key, by its fingerprint (<c>SHA256:…</c>).</summary>
    public static Revoking Key(string fingerprint) =>
        new(new Revocation(RevocationKind.Key, fingerprint), $"the key {fingerprint}, and every credential for it");

    /// <summary>A grant, by its ID.</summary>
    public static Revoking Grant(Guid id) => new(new Revocation(RevocationKind.Grant, id.ToString()), $"the grant {id}");

    /// <summary>A grant, saying what it gave.</summary>
    public static Revoking Grant(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new(new Revocation(RevocationKind.Grant, grant.Terms.Id.ToString()),
            $"the grant {grant.Terms.Id}, of {grant.Terms.Permission} to {grant.Terms.CredentialKey} serial {grant.Terms.CredentialSerial}")
        {
            Until = grant.Terms.ValidBefore,
        };
    }

    /// <summary>An X.509 certificate, by its SHA-256 hash.</summary>
    public static Revoking Certificate(Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new(new Revocation(RevocationKind.Certificate, certificate.Thumbprint), $"the certificate {certificate.Subject}, {certificate.Thumbprint}")
        {
            Until = certificate.NotAfter,
        };
    }
}

/// <summary>
/// Everything the trust store revokes: the lines of its <c>revoked</c> file, and the KRLs and CRLs in
/// <c>revocation-lists/</c>. A revocation only ever refuses, so none of them is checked for a signature: one someone
/// slipped in could do no more than one you wrote.
/// </summary>
public sealed class Revocations
{
    private readonly HashSet<Revocation> _entries;
    private readonly List<NamedList<SshRevocationList>> _krls;
    private readonly List<NamedList<X509Crl>> _crls;

    internal Revocations(IEnumerable<Revocation> entries, IEnumerable<NamedList<SshRevocationList>> krls, IEnumerable<NamedList<X509Crl>> crls)
    {
        Entries = [.. entries];
        _entries = [.. Entries];
        _krls = [.. krls];
        _crls = [.. crls];
        Lists = [.. _krls.Select(krl => krl.Name), .. _crls.Select(crl => crl.Name)];
    }

    /// <summary>Nothing revoked.</summary>
    public static Revocations None { get; } = new([], [], []);

    /// <summary>The lines of the <c>revoked</c> file.</summary>
    public IReadOnlyList<Revocation> Entries { get; }

    /// <summary>The names of the KRL and CRL files.</summary>
    public IReadOnlyList<string> Lists { get; }

    /// <summary>
    /// Why <paramref name="credential"/> is revoked, as a sentence, or null if it isn't: by its serial, its key, its
    /// authority's key, or a KRL.
    /// </summary>
    public string? WhyRevoked(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Serial != 0 && _entries.Contains(new Revocation(RevocationKind.Serial, credential.AuthorityFingerprint, credential.Serial)))
            return $"{credential.KeyId}'s credential, serial {credential.Serial}, is revoked.";
        if (_entries.Contains(new Revocation(RevocationKind.Key, credential.KeyFingerprint)))
            return $"{credential.KeyId}'s key, {credential.KeyFingerprint}, is revoked.";
        if (_entries.Contains(new Revocation(RevocationKind.Key, credential.AuthorityFingerprint)))
            return $"The key of the authority that issued {credential.KeyId}'s credential, {credential.AuthorityFingerprint}, is revoked.";
        foreach (var krl in _krls)
        {
            if (krl.List.WhyRevoked(credential) is { } why)
                return $"{credential.KeyId}'s credential is revoked by {krl.Name}: {why}.";
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="grant"/> is revoked, as a sentence, or null if it isn't: by its ID, its authority's key, or
    /// a revoked credential among those that authorised it (Q301).
    /// </summary>
    public string? WhyRevoked(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (_entries.Contains(new Revocation(RevocationKind.Grant, grant.Terms.Id.ToString())))
            return $"The grant {grant.Terms.Id}, of {grant.Terms.Permission}, is revoked.";
        if (_entries.Contains(new Revocation(RevocationKind.Key, grant.AuthorityFingerprint)))
            return $"The key that signed the grant {grant.Terms.Id}, {grant.AuthorityFingerprint}, is revoked.";
        foreach (var krl in _krls.Where(krl => krl.List.RevokesKey(grant.AuthorityKey.Span)))
            return $"The key that signed the grant {grant.Terms.Id} is revoked by {krl.Name}.";
        foreach (var authoriser in grant.Terms.AuthorisedBy)
        {
            if (WhyRevoked(authoriser) is { } why)
                return $"The grant {grant.Terms.Id}, of {grant.Terms.Permission}, falls with the credential that authorised it: {why}";
        }
        return null;
    }

    /// <summary>
    /// Why the key with the SHA-256 <paramref name="fingerprint"/> is revoked, as a clause ("its key, SHA256:…, is
    /// revoked."), or null if it isn't: listed in the <c>revoked</c> file, or in a KRL by its hash.
    /// </summary>
    public string? WhyKeyRevoked(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (_entries.Contains(new Revocation(RevocationKind.Key, fingerprint)))
            return $"its key, {fingerprint}, is revoked.";
        foreach (var krl in _krls)
        {
            if (krl.List.WhyRevoked(new CredentialName(fingerprint, 0, "")) is { } why && why.Contains("key", StringComparison.Ordinal))
                return $"its key is revoked by {krl.Name}: {why}.";
        }
        return null;
    }

    /// <summary>
    /// Why the credential <paramref name="name"/> names is revoked, as a sentence, or null if it isn't: by its serial,
    /// its key, its authority's key, or a KRL, as far as its fingerprints and serial tell.
    /// </summary>
    public string? WhyRevoked(CredentialName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Serial != 0 && _entries.Contains(new Revocation(RevocationKind.Serial, name.Authority, name.Serial)))
            return $"the credential for {name.Key}, serial {name.Serial}, is revoked.";
        if (_entries.Contains(new Revocation(RevocationKind.Key, name.Key)))
            return $"its key, {name.Key}, is revoked.";
        if (_entries.Contains(new Revocation(RevocationKind.Key, name.Authority)))
            return $"the key of the authority that issued it, {name.Authority}, is revoked.";
        foreach (var krl in _krls)
        {
            if (krl.List.WhyRevoked(name) is { } why)
                return $"the credential for {name.Key} is revoked by {krl.Name}: {why}.";
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="certificate"/> is revoked, as a sentence, or null if it isn't: by its hash, or by a CRL from
    /// its issuer that lists its serial.
    /// </summary>
    public string? WhyRevoked(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (_entries.Contains(new Revocation(RevocationKind.Certificate, hash)))
            return $"The certificate {certificate.Subject}, {hash}, is revoked.";
        var issuer = certificate.IssuerName.RawData;
        // .NET gives the serial big-endian, as the certificate has it; BouncyCastle's are positive numbers.
        var serial = new Org.BouncyCastle.Math.BigInteger(1, certificate.SerialNumberBytes.ToArray());
        foreach (var crl in _crls.Where(crl => crl.List.IssuerDN.GetEncoded().AsSpan().SequenceEqual(issuer)))
        {
            if (crl.List.GetRevokedCertificate(serial) is not null)
                return $"The certificate {certificate.Subject}, serial {certificate.SerialNumber}, is revoked by {crl.Name}.";
        }
        return null;
    }

    /// <summary>When the CRLs from <paramref name="issuer"/>'s name say they'll next be updated: the earliest, or null if none say.</summary>
    public DateTime? NextUpdate(X500DistinguishedName issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        return _crls
            .Where(crl => crl.List.IssuerDN.GetEncoded().AsSpan().SequenceEqual(issuer.RawData))
            .Select(crl => crl.List.NextUpdate)
            .Min();
    }

    /// <summary>
    /// The CRLs whose next update was due before <paramref name="when"/>, by file name, with when it was due. A
    /// certificate from their issuer can't be known not to be revoked, so <c>Verify</c> fails it (Q274).
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> OutOfDateAt(DateTime when)
    {
        when = when.ToUniversalTime();
        return _crls
            .Where(crl => crl.List.NextUpdate is { } due && due.ToUniversalTime() < when)
            .ToDictionary(crl => crl.Name, crl => crl.List.NextUpdate!.Value.ToUniversalTime(), StringComparer.Ordinal);
    }

    /// <summary>
    /// What a KRL (<c>.krl</c>) or CRL (<c>.crl</c>, DER or PEM) is, in words, having read it. Throws
    /// <see cref="FormatException"/>, naming it, if it can't be read.
    /// </summary>
    public static string DescribeList(string name, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contents);
        return Path.GetExtension(name) == ".krl"
            ? $"key revocation list {name}, version {SshRevocationList.Parse(contents).Version}"
            : $"certificate revocation list {name}, from {ReadCrl(name, contents).IssuerDN}";
    }

    /// <summary>Reads a CRL, as DER or PEM. Throws <see cref="FormatException"/>, naming it, if it can't.</summary>
    internal static X509Crl ReadCrl(string name, byte[] contents)
    {
        try
        {
            return new X509CrlParser().ReadCrl(contents) ?? throw new FormatException($"{name} has no CRL in it.");
        }
        catch (Exception exception) when (exception is not FormatException and not OutOfMemoryException)
        {
            throw new FormatException($"{name} isn't a CRL Meep can read: {exception.Message}", exception);
        }
    }
}

/// <summary>A revocation list and the name of its file.</summary>
internal sealed record NamedList<T>(string Name, T List);
