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
/// <para>
/// The daemon, <c>meep run</c> and <c>meep trust</c> may all change the store at once, so every change holds an
/// exclusive lock on <c>.lock</c> while it works, and every file is written whole and renamed into place, so a
/// <see cref="TrustStoreWatcher"/> reloading meanwhile never reads half of one.
/// </para>
/// </remarks>
public sealed class TrustFolder
{
    // How long to wait for another process changing the store before giving up.
    private static readonly TimeSpan s_lockWait = TimeSpan.FromSeconds(10);

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
        using var held = Lock();
        CreateFiles();
    }

    // Create's work, for callers that hold the lock already.
    private void CreateFiles()
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
        using var held = Lock();
        if (System.IO.File.Exists(file))
            throw new IOException($"There's a root called {name} already, in {file}.");
        Write(file, Encoding.ASCII.GetBytes(PemEncoding.WriteString("CERTIFICATE", der) + "\n"));
        return file;
    }

    /// <summary>
    /// Adds a line to <see cref="Revoked"/>, after a comment saying what it was, when, and until when it's needed, if
    /// that's known (<see cref="StoreTidying.Until"/>).
    /// </summary>
    public void Revoke(Revocation revocation, string what, DateTime? until = null)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(what);
        var needed = until is { } end ? $"; {StoreTidying.Until(end)}" : "";
        AppendLine(Revoked, $"# {what.ReplaceLineEndings(" ")}, revoked {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z{needed}\n{revocation}");
    }

    /// <summary>Adds a revocation to <see cref="Revoked"/>, after a comment saying what it revokes, when, and until when.</summary>
    public void Revoke(Revoking revoking)
    {
        ArgumentNullException.ThrowIfNull(revoking);
        Revoke(revoking.Revocation, revoking.What, revoking.Until);
    }

    /// <summary>
    /// Takes out of <see cref="Grants"/> and <see cref="Revoked"/> what's no longer needed, as of
    /// <paramref name="now"/>, as <see cref="StoreTidying"/> decides (Q305), holding the lock. Throws
    /// <see cref="TrustStoreException"/> if the store can't be read.
    /// </summary>
    public Tidied Tidy(DateTime now)
    {
        using var held = Lock();
        var tidying = new StoreTidying(TrustStore.Load(this), now);
        var grants = StoreLines.Parse(System.IO.File.Exists(Grants) ? System.IO.File.ReadAllText(Grants) : "");
        var revoked = StoreLines.Parse(System.IO.File.Exists(Revoked) ? System.IO.File.ReadAllText(Revoked) : "");
        var tidied = new Tidied(grants.RemoveWhere(tidying.WhyGrantGoes), revoked.RemoveWhere(tidying.WhyRevocationGoes(grants)));
        if (tidied.Grants.Count > 0)
            Write(Grants, Encoding.UTF8.GetBytes(grants.ToString()));
        if (tidied.Revocations.Count > 0)
            Write(Revoked, Encoding.UTF8.GetBytes(revoked.ToString()));
        return tidied;
    }

    /// <summary>Copies a KRL or CRL into <see cref="RevocationLists"/>, under its own name, and returns where it went.</summary>
    public string AddRevocationList(string name, ReadOnlySpan<byte> contents)
    {
        if (!IsFileName(name) || System.IO.Path.GetExtension(name) is not (".krl" or ".crl"))
            throw new FormatException($"{name} can't name a revocation list: it ends .krl or .crl, with letters, digits, ., - and _ before.");
        var file = System.IO.Path.Combine(RevocationLists, name);
        using var held = Lock();
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
        using var held = Lock();
        CreateFiles();
        var last = System.IO.File.Exists(Serial) && ulong.TryParse(System.IO.File.ReadAllText(Serial).Trim(), out var read) ? read : 0;
        var next = last + 1;
        Write(Serial, Encoding.ASCII.GetBytes($"{next}\n"));
        return next;
    }

    /// <summary>
    /// Signs <paramref name="terms"/> with this machine's authority key, in the <c>meep-grant</c> namespace, as
    /// <c>Authorise</c> does. Throws <see cref="TrustStoreException"/> if there's no authority key, or it can't be
    /// read or used.
    /// </summary>
    public Grant SignGrant(GrantTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        string text;
        try
        {
            text = System.IO.File.ReadAllText(AuthorityKey);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TrustStoreException($"This machine has no authority key to sign grants with ({AuthorityKey}). Make one with meep trust init.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TrustStoreException($"Can't read {AuthorityKey}: {exception.Message}", exception);
        }
        SshKeyPair key;
        try
        {
            key = SshKeyPair.FromOpenSsh("this machine", text);
        }
        catch (FormatException exception)
        {
            throw new TrustStoreException($"{AuthorityKey} can't sign grants: {exception.Message}", exception);
        }
        using (key)
            return Grant.Signed(terms, key.Sign(terms.ToSignedData(), Grant.Namespace).ToBinary());
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
            bool othersCanWrite;
            try
            {
                othersCanWrite = PrivateFolders.OthersCanWrite(path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A file a writer in another process renamed into place, or away, since the folder was listed.
                continue;
            }
            if (othersCanWrite)
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

    // Adds a line to a file by writing the whole file again and renaming it into place, holding the lock, so a reload
    // never sees half a line, and two processes adding at once don't lose either's.
    private void AppendLine(string file, string line)
    {
        using var held = Lock();
        CreateFiles();
        var before = System.IO.File.Exists(file) ? System.IO.File.ReadAllBytes(file) : [];
        var added = Encoding.UTF8.GetBytes(line + "\n");
        var separator = before.Length > 0 && before[^1] != (byte)'\n' ? "\n"u8.ToArray() : [];
        Write(file, [.. before, .. separator, .. added]);
    }

    /// <summary>The lock file every change to the store holds, so the daemon, meep run and meep trust can all change it.</summary>
    public string LockFile => File(".lock");

    // Holds the store's lock until disposed: an exclusive lock on .lock, which other processes, and other changes in
    // this one, wait for. On macOS and Linux this is an advisory lock (flock) that only Meep's own writers take.
    private FileStream Lock()
    {
        PrivateFolders.Create(Path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFolders.File;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(LockFile, options);
            }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetElapsedTime(started) < s_lockWait)
            {
                Thread.Sleep(Random.Shared.Next(5, 25));
            }
            catch (IOException exception)
            {
                throw new IOException($"The trust store in {Path} has been being changed by something else for {s_lockWait.TotalSeconds:0} seconds, so it was left alone. Try again.", exception);
            }
        }
    }

    // Writes a whole file, kept to the user, by renaming a new file into place, so a reload never sees half of it. The
    // caller holds the lock.
    private void Write(string file, byte[] contents)
    {
        PrivateFolders.Create(System.IO.Path.GetDirectoryName(file)!);
        var temporary = file + ".new";
        using (var stream = new FileStream(temporary, PrivateFolders.NewFile(FileMode.Create)))
            stream.Write(contents);
        System.IO.File.Move(temporary, file, overwrite: true);
    }
}
