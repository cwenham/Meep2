using System.Security.Cryptography;
using System.Text;
using Meep2.SDK.Security;
using Meep2.Shared;

namespace Meep2.Security;

/// <summary>
/// The trust store's folder, <c>~/.meep/trust</c>, and the files in it, kept to the user (S7): folders 700 and files
/// 600. It's where things are written; <see cref="TrustStore"/> reads them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>authority.key</c> and <c>authority.key.pub</c>: this machine's own Ed25519 authority key, made by
/// <c>meep trust init</c>, which issues credentials and signs grants.</item>
/// <item><c>authorities</c>: keys trusted to issue credentials and sign grants, in <c>allowed_signers</c> form with
/// namespaces.</item>
/// <item><c>roots/</c>: trusted X.509 root and CA certificates, one to a file.</item>
/// <item><c>revoked</c>: Meep's own list of revoked credentials, keys, grants and certificates.</item>
/// <item><c>revocation-lists/</c>: OpenSSH key revocation lists (<c>.krl</c>) and X.509 CRLs (<c>.crl</c>).</item>
/// <item><c>grants</c>: grants recorded by <c>Authorise</c> (Q256), one to a line.</item>
/// <item><c>permissions.meep</c>: permission definitions (Q257).</item>
/// <item><c>serial</c>: the last serial <c>meep trust issue</c> gave a credential.</item>
/// </list>
/// </remarks>
public sealed class TrustFolder
{
    /// <summary>A trust store in <paramref name="path"/>.</summary>
    public TrustFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>The trust store in Meep's folder, <paramref name="meepHome"/>: its <c>trust</c> folder.</summary>
    public static TrustFolder In(string meepHome) => new(System.IO.Path.Combine(meepHome, "trust"));

    /// <summary>The folder.</summary>
    public string Path { get; }

    /// <summary>This machine's own authority's private key, in OpenSSH's format.</summary>
    public string AuthorityKey => File("authority.key");

    /// <summary>This machine's own authority's public key.</summary>
    public string AuthorityPublicKey => AuthorityKey + ".pub";

    /// <summary>The keys trusted to issue credentials and sign grants.</summary>
    public string Authorities => File("authorities");

    /// <summary>The folder of trusted X.509 roots and CAs.</summary>
    public string Roots => File("roots");

    /// <summary>Meep's own list of what's revoked.</summary>
    public string Revoked => File("revoked");

    /// <summary>The folder of KRLs and CRLs.</summary>
    public string RevocationLists => File("revocation-lists");

    /// <summary>The recorded grants.</summary>
    public string Grants => File("grants");

    /// <summary>The permission definitions.</summary>
    public string Permissions => File("permissions.meep");

    /// <summary>The last serial given to a credential by <c>meep trust issue</c>.</summary>
    public string Serial => File("serial");

    /// <summary>Whether <c>meep trust init</c> has set it up: there's an authority key.</summary>
    public bool IsSetUp => System.IO.File.Exists(AuthorityKey);

    /// <summary>
    /// Makes the folder and its two subfolders, kept to the user, and the files that start empty, each with a comment
    /// saying what it's for. Leaves what's there already alone, though it narrows the folders if others can reach in.
    /// </summary>
    public void Create()
    {
        PrivateFolders.Create(Path);
        PrivateFolders.Create(Roots);
        PrivateFolders.Create(RevocationLists);
        CreateWith(Authorities, "# Keys trusted to issue credentials (meep-credential) and sign grants (meep-grant), as allowed_signers has them.\n");
        CreateWith(Revoked, "# What's revoked: serial <authority> <serial>, key <fingerprint>, grant <ID> or certificate <SHA-256>, a line each.\n");
        CreateWith(Grants, "# Grants recorded by Authorise, a line each.\n");
        CreateWith(Permissions, """
            // Permission definitions, one to a line, as in:
            //   #permission ReadNotes
            //   #permission WriteNotes authority=home requires=ReadNotes
            // A pipeline's own #permission line can only narrow one of these.

            """);
    }

    /// <summary>
    /// Adds a key to <see cref="Authorities"/> as <c>name namespaces="…" type base64</c>, trusted for these
    /// namespaces.
    /// </summary>
    public void AddAuthority(string name, PublicKey key, IReadOnlyList<string> namespaces)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(namespaces);
        if (!Permission.IsAuthorityName(name))
            throw new FormatException($"{name} can't be an authority's name: it has no spaces, quotes, = or # in it.");
        AppendLine(Authorities, $"{name} namespaces=\"{string.Join(',', namespaces)}\" {key.Text}");
    }

    /// <summary>Writes a trusted root or CA as <c>roots/name.pem</c>, and returns the file.</summary>
    public string AddRoot(string name, ReadOnlySpan<byte> der)
    {
        if (!IsFileName(name))
            throw new FormatException($"{name} can't name a root: use letters, digits, ., - and _.");
        var file = System.IO.Path.Combine(Roots, name + ".pem");
        if (System.IO.File.Exists(file))
            throw new IOException($"There's a root called {name} already, in {file}.");
        Write(file, Encoding.ASCII.GetBytes(PemEncoding.WriteString("CERTIFICATE", der) + "\n"));
        return file;
    }

    /// <summary>Adds a line to <see cref="Revoked"/>, after a comment saying what it was and when.</summary>
    public void Revoke(Revocation revocation, string what)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(what);
        AppendLine(Revoked, $"# {what.ReplaceLineEndings(" ")}, revoked {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z\n{revocation}");
    }

    /// <summary>Copies a KRL or CRL into <see cref="RevocationLists"/>, under its own name, and returns where it went.</summary>
    public string AddRevocationList(string name, ReadOnlySpan<byte> contents)
    {
        if (!IsFileName(name) || System.IO.Path.GetExtension(name) is not (".krl" or ".crl"))
            throw new FormatException($"{name} can't name a revocation list: it ends .krl or .crl, with letters, digits, ., - and _ before.");
        var file = System.IO.Path.Combine(RevocationLists, name);
        Write(file, contents.ToArray());
        return file;
    }

    /// <summary>Records a grant in <see cref="Grants"/>, so it applies whenever its credential is verified (Q256).</summary>
    public void RecordGrant(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        AppendLine(Grants, $"# {grant.Terms.Permission} for {grant.Terms.CredentialKey} serial {grant.Terms.CredentialSerial}\n{grant.ToText()}");
    }

    /// <summary>The next serial for a credential: one more than the last given, kept in <see cref="Serial"/>.</summary>
    public ulong NextSerial()
    {
        Create();
        // Two at once can't both open it, rather than giving the same serial.
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFolders.File;
        using var file = new FileStream(Serial, options);
        using var reader = new StreamReader(file, leaveOpen: true);
        var last = ulong.TryParse(reader.ReadToEnd().Trim(), out var read) ? read : 0;
        var next = last + 1;
        file.SetLength(0);
        file.Write(Encoding.ASCII.GetBytes($"{next}\n"));
        return next;
    }

    /// <summary>
    /// Throws <see cref="TrustStoreException"/> if anyone else could change what the store trusts, as OpenSSH refuses
    /// an <c>authorized_keys</c> others can write to, or read the authority's private key.
    /// </summary>
    public void CheckPrivate()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(Path))
            return;
        var parent = System.IO.Path.GetDirectoryName(Path)!;
        foreach (var path in new[] { parent, Path, Roots, RevocationLists }.Where(Directory.Exists)
            .Concat(Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)))
        {
            if (PrivateFolders.OthersCanWrite(path))
                throw new TrustStoreException($"{path} can be changed by other users, so Meep doesn't trust what's in it. Make it yours alone with chmod go-w \"{path}\".");
        }
        if (System.IO.File.Exists(AuthorityKey)
            && (System.IO.File.GetUnixFileMode(AuthorityKey) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
        {
            throw new TrustStoreException($"{AuthorityKey} is this machine's authority's private key, and others can read it. Make it yours alone with chmod 600 \"{AuthorityKey}\".");
        }
    }

    private string File(string name) => System.IO.Path.Combine(Path, name);

    private static bool IsFileName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private void CreateWith(string file, string text)
    {
        if (!System.IO.File.Exists(file))
            Write(file, Encoding.UTF8.GetBytes(text));
    }

    private void AppendLine(string file, string line)
    {
        Create();
        using var stream = new FileStream(file, PrivateFolders.NewFile(FileMode.Append));
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
    }

    // Writes a whole file, kept to the user, by renaming a new file into place, so a reload never sees half of it.
    private void Write(string file, byte[] contents)
    {
        PrivateFolders.Create(System.IO.Path.GetDirectoryName(file)!);
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, PrivateFolders.NewFile(FileMode.Create)))
            stream.Write(contents);
        System.IO.File.Move(temporary, file, overwrite: true);
    }
}
