using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.SDK.Security;
using Org.BouncyCastle.X509;

namespace Meep2.Security;

/// <summary>
/// What this machine trusts, read from its <see cref="TrustFolder"/>: the authorities that issue credentials and sign
/// grants, the X.509 roots, what's revoked, the grants recorded, and the permissions defined. It doesn't change once
/// read; <see cref="Reload"/> reads the folder again into a new one, with a higher <see cref="Version"/>.
/// </summary>
/// <remarks>
/// <c>Verify</c> checks against one, and records its <see cref="Version"/> and <see cref="RevocationVersion"/> in the
/// <see cref="VerifiedMessage"/>, so a gate can tell when to look again (Q259).
/// </remarks>
public sealed class TrustStore
{
    /// <summary>The namespace an authority's key must be trusted for to issue credentials.</summary>
    public const string CredentialNamespace = "meep-credential";

    /// <summary>The namespace an authority's key must be trusted for to sign grants: <see cref="Grant.Namespace"/>.</summary>
    public const string GrantNamespace = Grant.Namespace;

    private static readonly string[] s_rootExtensions = [".pem", ".crt", ".cer", ".der"];

    private readonly Dictionary<string, List<Grant>> _grantsByKey;
    private readonly Contents _contents;

    private TrustStore(TrustFolder folder, Contents contents, long version, long revocationVersion)
    {
        Folder = folder;
        _contents = contents;
        Version = version;
        RevocationVersion = revocationVersion;
        _grantsByKey = Grants.GroupBy(grant => grant.Terms.CredentialKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    /// <summary>Where it was read from.</summary>
    public TrustFolder Folder { get; }

    /// <summary>
    /// Its version: 1 when first loaded, and one more each time a reload finds something changed. Kept only while
    /// Meep runs, as verifications are.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Raised only when a reload takes something away: a revocation added or changed, or an authority, root, grant or
    /// permission definition removed or changed. Adding one leaves it alone, since nothing already verified becomes
    /// wrong. A gate checks a verification again when this has changed since (Q259).
    /// </summary>
    public long RevocationVersion { get; }

    /// <summary>This machine's own authority's public key, made by <c>meep trust init</c>, or null before then.</summary>
    public PublicKey? OwnAuthority => _contents.OwnAuthority;

    /// <summary>Whether <c>meep trust init</c> has set it up.</summary>
    public bool IsSetUp => OwnAuthority is not null;

    /// <summary>The authorities' keys, named, with the namespaces each is trusted for.</summary>
    public IReadOnlyList<PublicKey> Authorities => _contents.Authorities;

    /// <summary>The trusted X.509 roots and CAs.</summary>
    public IReadOnlyList<TrustedRoot> Roots => _contents.Roots;

    /// <summary>What's revoked.</summary>
    public Revocations Revocations => _contents.Revocations;

    /// <summary>Every grant recorded, unchecked: <c>Verify</c> checks each one's signature and authority.</summary>
    public IReadOnlyList<Grant> Grants => _contents.Grants;

    /// <summary>The permissions defined.</summary>
    public PermissionDefinitions Permissions => _contents.Permissions;

    /// <summary>What's wrong with the store that doesn't stop it loading, a sentence each, such as a definition naming an authority that isn't there.</summary>
    public IReadOnlyList<string> Problems => Permissions.Problems;

    /// <summary>
    /// Reads the trust store in <paramref name="folder"/>. A folder that isn't there is an empty store. Throws
    /// <see cref="TrustStoreException"/>, saying which file and why, if others could change it or a file can't be read.
    /// </summary>
    public static TrustStore Load(TrustFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new TrustStore(folder, Contents.Read(folder), 1, 1);
    }

    /// <summary>
    /// Reads the folder again. Returns this store if nothing in it has changed, or a new one with the next
    /// <see cref="Version"/>, and the next <see cref="RevocationVersion"/> if anything was taken away. Throws as
    /// <see cref="Load"/> does, and then this store still stands.
    /// </summary>
    public TrustStore Reload()
    {
        var contents = Contents.Read(Folder);
        if (contents.Digest.AsSpan().SequenceEqual(_contents.Digest))
            return this;
        return new TrustStore(Folder, contents, Version + 1, contents.TakesAwayFrom(_contents) ? RevocationVersion + 1 : RevocationVersion);
    }

    /// <summary>The authority called <paramref name="name"/> in the <c>authorities</c> file, or null.</summary>
    public PublicKey? Authority(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Authorities.FirstOrDefault(key => key.Name == name);
    }

    /// <summary>The trusted authority that issued <paramref name="credential"/>, if its key may issue credentials, or null.</summary>
    public PublicKey? AuthorityOf(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Trusted(credential.AuthorityKey.Span, CredentialNamespace);
    }

    /// <summary>The trusted authority that signed <paramref name="grant"/>, if its key may sign grants, or null.</summary>
    public PublicKey? AuthorityOf(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Trusted(grant.AuthorityKey.Span, GrantNamespace);
    }

    /// <summary>
    /// The grants recorded for <paramref name="credential"/>: those naming its key, serial and authority. They're
    /// unchecked, as <see cref="Grants"/> are.
    /// </summary>
    public IReadOnlyList<Grant> GrantsFor(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return _grantsByKey.TryGetValue(credential.KeyFingerprint, out var grants)
            ? [.. grants.Where(grant => grant.Terms.AppliesTo(credential))]
            : [];
    }

    /// <summary>The roots as a collection, for <see cref="X509ChainPolicy.CustomTrustStore"/>.</summary>
    public X509Certificate2Collection RootCollection() => [.. Roots.Select(root => root.Certificate)];

    private PublicKey? Trusted(ReadOnlySpan<byte> key, string ns)
    {
        foreach (var authority in Authorities)
        {
            if (authority.Blob.AsSpan().SequenceEqual(key) && authority.MaySignFor(ns))
                return authority;
        }
        return null;
    }

    // Everything read from the folder, and a digest of the files it came from.
    private sealed record Contents(
        PublicKey? OwnAuthority,
        IReadOnlyList<PublicKey> Authorities,
        IReadOnlyList<TrustedRoot> Roots,
        Revocations Revocations,
        IReadOnlyList<Grant> Grants,
        PermissionDefinitions Permissions,
        byte[] Digest,
        byte[] RevocationDigest)
    {
        // Whether these contents, read after previous, no longer have something previous had, or revoke something new.
        public bool TakesAwayFrom(Contents previous)
        {
            if (!RevocationDigest.AsSpan().SequenceEqual(previous.RevocationDigest))
                return true;
            if (previous.OwnAuthority is { } own && (OwnAuthority is null || !own.SameKeyAs(OwnAuthority)))
                return true;
            return Lost(previous.Authorities, Authorities, key => $"{key.Name} {key.Text} {string.Join(',', key.Namespaces ?? ["*"])}")
                || Lost(previous.Roots, Roots, root => root.Certificate.Thumbprint)
                || Lost(previous.Grants, Grants, grant => grant.ToText())
                || Lost(previous.Permissions.All, Permissions.All, permission => permission.ToDirective());
        }

        // Whether anything in before, by its description, isn't in after.
        private static bool Lost<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, string> describe)
        {
            var kept = after.Select(describe).ToHashSet(StringComparer.Ordinal);
            return before.Any(item => !kept.Contains(describe(item)));
        }

        public static Contents Read(TrustFolder folder)
        {
            folder.CheckPrivate();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var revocationDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string? Text(string file, IncrementalHash? also = null) => Read(file, digest, also) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

            var own = Text(folder.AuthorityPublicKey) is { } ownText ? Wrap(folder.AuthorityPublicKey, () => PublicKey.Parse("this machine", ownText)) : null;
            var authorities = Text(folder.Authorities) is { } authoritiesText ? ReadAuthorities(folder.Authorities, authoritiesText) : [];
            var roots = Files(folder.Roots, s_rootExtensions)
                .Select(file => new TrustedRoot(Path.GetFileNameWithoutExtension(file), Wrap(file, () => X509CertificateLoader.LoadCertificate(Read(file, digest)!))))
                .ToList();

            var entries = Lines(folder.Revoked, Text(folder.Revoked, revocationDigest), Revocation.Parse);
            var krls = new List<NamedList<SshRevocationList>>();
            var crls = new List<NamedList<X509Crl>>();
            foreach (var file in Files(folder.RevocationLists, [".krl", ".crl"]))
            {
                var bytes = Read(file, digest, revocationDigest)!;
                var name = Path.GetFileName(file);
                if (Path.GetExtension(file) == ".krl")
                    krls.Add(new(name, Wrap(file, () => SshRevocationList.Parse(bytes))));
                else
                    crls.Add(new(name, Wrap(file, () => Revocations.ReadCrl(name, bytes))));
            }

            var grants = Lines(folder.Grants, Text(folder.Grants), Grant.Parse);
            var permissionsText = Text(folder.Permissions);
            var permissions = permissionsText is null
                ? new PermissionDefinitions([], authorities.Select(key => key.Name))
                : Wrap(folder.Permissions, () => PermissionDefinitions.Parse(permissionsText, authorities.Select(key => key.Name)));
            return new Contents(
                own, authorities, roots, new Revocations(entries, krls, crls), grants, permissions,
                digest.GetHashAndReset(), revocationDigest.GetHashAndReset());
        }

        // The authorities file, as TrustedKeys reads it, with each name used once, since definitions refer to them.
        private static List<PublicKey> ReadAuthorities(string file, string text)
        {
            var keys = TrustedKeys.Parse(file, text.Split('\n'));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys.Keys)
            {
                if (!Permission.IsAuthorityName(key.Name))
                    throw new TrustStoreException($"{file}: {key.Name} can't be an authority's name: it has no quotes, = or # in it.");
                if (!names.Add(key.Name))
                    throw new TrustStoreException($"{file} has two authorities called {key.Name}. Permissions name authorities, so each needs a name of its own.");
            }
            return [.. keys.Keys];
        }

        // A file's bytes, added to the digests with its name, or null if it isn't there.
        private static byte[]? Read(string file, IncrementalHash digest, IncrementalHash? also = null)
        {
            if (!File.Exists(file))
                return null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new TrustStoreException($"Can't read {file}: {exception.Message}", exception);
            }
            foreach (var hash in also is null ? new[] { digest } : new[] { digest, also })
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0"));
                hash.AppendData(bytes);
            }
            return bytes;
        }

        // The files in a folder with these extensions, in order of name, so the digest doesn't depend on the disk.
        private static IEnumerable<string> Files(string folder, string[] extensions) => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [];

        // Each line that isn't blank or a # comment, read, naming the line if one can't be.
        private static List<T> Lines<T>(string file, string? text, Func<string, T> parse)
        {
            var items = new List<T>();
            var number = 0;
            foreach (var line in (text ?? "").Split('\n'))
            {
                number++;
                if (line.Trim() is { Length: 0 } or ['#', ..])
                    continue;
                items.Add(Wrap($"{file}, line {number}", () => parse(line.Trim())));
            }
            return items;
        }

        private static T Wrap<T>(string where, Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                throw new TrustStoreException($"{where}: {exception.Message}", exception);
            }
        }
    }
}

/// <summary>A trusted X.509 root or CA, named after its file in <c>roots/</c>.</summary>
/// <param name="Name">The file's name, without its extension.</param>
/// <param name="Certificate">The certificate.</param>
public sealed record TrustedRoot(string Name, X509Certificate2 Certificate)
{
    /// <summary>Why <paramref name="certificate"/> can't be a root, or null if it can: it must be a CA's.</summary>
    public static string? WhyNot(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true }
            ? null
            : $"{certificate.Subject} isn't a CA's certificate, so it can't be a root. Trust the CA that issued it instead.";
    }

    /// <summary>A name for a root's file made from any text, such as a file's name or a certificate's subject.</summary>
    public static string NameFrom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var name = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')]).Trim('.', '-');
        return name.Length == 0 ? "root" : name;
    }
}

/// <summary>
/// What Meep trusts can't be used: the trust store, or an allowed-signers file, could be changed by others, or a file
/// in it can't be read.
/// </summary>
public sealed class TrustStoreException : Exception
{
    /// <summary>Says why.</summary>
    public TrustStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Says why, and what went wrong underneath.</summary>
    public TrustStoreException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>For serialisers and analysers.</summary>
    public TrustStoreException()
    {
    }
}
