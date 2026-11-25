using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meep2.Plugins;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Shared;
using PublicKey = Meep2.Security.PublicKey;

namespace Meep2.Cli;

/// <summary>
/// meep trust: the trust store that credentials, certificates and permissions are checked against (permissions,
/// phase 2). It's set up with <c>init</c>, given authorities and roots, told what's revoked, and can issue credentials
/// with its own authority for getting started.
/// </summary>
internal static class TrustCommands
{
    public const string AddAuthorityUsage =
        "Trust an authority as: meep trust add-authority <name> ssh-ed25519 AAAA…, or meep trust add-authority <name> <ca.pub>. Add --grants if its grants count too.";

    public const string AddRootUsage = "Trust a root or CA certificate as: meep trust add-root <file.pem> [<name>].";

    public const string RevokeUsage =
        "Revoke a credential (its -cert.pub or .credential file), a key (its .pub file or SHA256: fingerprint), a grant (its ID), a certificate (its .pem file), or add a list (a .krl or .crl file), as: meep trust revoke <it> [--key].";

    public const string IssueUsage =
        "Issue a credential as: meep trust issue <key.pub> [--id <name>] [--principals <a,b>] [--valid <+52w | always | from:to>] [--permission <name[:scope]>]... [--bearer] [--out <file>].";

    // meep trust init [--name <name>]: makes the folder and this machine's authority key, and trusts it.
    public static async Task<int> InitAsync(string? name, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var folder = TrustFolder.In(options.Home);
        name ??= MachineName();
        if (!Permission.IsAuthorityName(name))
            return await FailAsync(error, $"{name} can't be an authority's name: it has no spaces, quotes, = or # in it.", 2).ConfigureAwait(false);
        // Meep's folder may have been made by something else, or under a umask of 002, so others could reach in and
        // the store wouldn't load (Q284). Narrowed first, so the load below can succeed.
        foreach (var narrowed in new[] { options.Home, folder.Path }.Where(PrivateFolders.Narrow))
            await output.WriteLineAsync($"Made {narrowed} yours alone (700), since others could reach into it.").ConfigureAwait(false);
        if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
            return 1;
        if (store.IsSetUp)
        {
            await output.WriteLineAsync($"The trust store in {folder.Path} is set up already. This machine's authority key is {store.OwnAuthority!.Fingerprint}.").ConfigureAwait(false);
            return 0;
        }
        if (store.Authority(name) is not null)
            return await FailAsync(error, $"There's an authority called {name} already. Choose another with --name.", 2).ConfigureAwait(false);

        folder.Create();
        try
        {
            var made = await SshKeygenAsync(["-q", "-t", "ed25519", "-N", "", "-C", $"meep authority {name}", "-f", folder.AuthorityKey], cancellationToken).ConfigureAwait(false);
            if (!made.Succeeded)
                return await FailAsync(error, $"ssh-keygen couldn't make the authority key: {made.Said}", 1).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return await FailAsync(error, NoSshKeygen, 1).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder.AuthorityPublicKey, PrivateFolders.File); // ssh-keygen makes it 644
        var key = PublicKey.Parse(name, await File.ReadAllTextAsync(folder.AuthorityPublicKey, cancellationToken).ConfigureAwait(false));
        folder.AddAuthority(name, key, [TrustStore.CredentialNamespace, TrustStore.GrantNamespace]);
        await output.WriteLineAsync($"""
            Set up the trust store in {folder.Path}.
            This machine's authority is {name}, with the key {key.Fingerprint}. It issues credentials and signs grants.
            Define permissions in {folder.Permissions}, and issue yourself a credential with:
              meep trust issue ~/.ssh/id_ed25519.pub --permission <name>
            """).ConfigureAwait(false);
        return 0;
    }

    // meep trust add-authority <name> <type> <key> | <name> <file.pub> [--grants]
    public static async Task<int> AddAuthorityAsync(string[] words, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var grants = words.Contains("--grants");
        words = [.. words.Where(word => word != "--grants")];
        PublicKey key;
        try
        {
            key = words switch
            {
                [var name, var file] => PublicKey.Parse(name, await ReadPublicKeyAsync(file, cancellationToken).ConfigureAwait(false)),
                [var name, var type, var base64, ..] => PublicKey.Parse(name, $"{type} {base64}"),
                _ => throw new FormatException(AddAuthorityUsage),
            };
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, exception.Message, 2).ConfigureAwait(false);
        }
        if (!Permission.IsAuthorityName(key.Name))
            return await FailAsync(error, $"{key.Name} can't be an authority's name: it has no spaces, quotes, = or # in it.", 2).ConfigureAwait(false);

        var folder = TrustFolder.In(options.Home);
        if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
            return 1;
        if (store.Authorities.FirstOrDefault(key.SameKeyAs) is { } already)
        {
            await output.WriteLineAsync($"You trust {key.Fingerprint} already, as the authority {already.Name}.").ConfigureAwait(false);
            return 0;
        }
        if (store.Authority(key.Name) is not null)
            return await FailAsync(error, $"There's an authority called {key.Name} already, with another key. Give this one another name.", 2).ConfigureAwait(false);

        string[] namespaces = grants ? [TrustStore.CredentialNamespace, TrustStore.GrantNamespace] : [TrustStore.CredentialNamespace];
        folder.AddAuthority(key.Name, key, namespaces);
        await output.WriteLineAsync(grants
            ? $"Credentials issued by {key.Name}'s {key.Type} key {key.Fingerprint}, and grants it signs, can be verified now."
            : $"Credentials issued by {key.Name}'s {key.Type} key {key.Fingerprint} can be verified now.").ConfigureAwait(false);
        return 0;
    }

    // meep trust add-root <file> [<name>]: a CA certificate, written to roots/ as PEM.
    public static async Task<int> AddRootAsync(string file, string? name, PluginOptions options, TextWriter output, TextWriter error)
    {
        file = HomeFolder.Expand(file);
        if (!File.Exists(file))
            return await FailAsync(error, $"Can't find {file}. {AddRootUsage}", 1).ConfigureAwait(false);
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificateFromFile(file);
        }
        catch (CryptographicException exception)
        {
            return await FailAsync(error, $"{file} isn't a certificate Meep can read: {exception.Message}", 1).ConfigureAwait(false);
        }
        using (certificate)
        {
            if (TrustedRoot.WhyNot(certificate) is { } notRoot)
                return await FailAsync(error, notRoot, 1).ConfigureAwait(false);

            var folder = TrustFolder.In(options.Home);
            if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
                return 1;
            if (store.Roots.FirstOrDefault(root => root.Certificate.Thumbprint == certificate.Thumbprint) is { } already)
            {
                await output.WriteLineAsync($"You trust {certificate.Subject} already, as the root {already.Name}.").ConfigureAwait(false);
                return 0;
            }
            name ??= FileName(Path.GetFileNameWithoutExtension(file));
            string written;
            try
            {
                written = folder.AddRoot(name, certificate.RawData);
            }
            catch (Exception exception) when (exception is FormatException or IOException)
            {
                return await FailAsync(error, exception.Message, 2).ConfigureAwait(false);
            }
            await output.WriteLineAsync($"Certificates that chain to {certificate.Subject} can be verified now. It's in {written}.").ConfigureAwait(false);
            if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
                await output.WriteLineAsync($"It isn't self-signed: it's a CA issued by {certificate.Issuer}, which isn't trusted by this.").ConfigureAwait(false);
            return 0;
        }
    }

    // meep trust revoke <credential | key | fingerprint | grant ID | certificate | file.krl | file.crl> [--key]
    public static async Task<int> RevokeAsync(string[] words, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var byKey = words.Contains("--key");
        words = [.. words.Where(word => word != "--key")];
        if (words.Length == 0)
            return await FailAsync(error, RevokeUsage, 2).ConfigureAwait(false);
        var folder = TrustFolder.In(options.Home);
        if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
            return 1;

        var thing = string.Join(' ', words);
        var file = HomeFolder.Expand(words[0]);
        if (words.Length == 1 && File.Exists(file) && Path.GetExtension(file) is ".krl" or ".crl")
            return await AddRevocationListAsync(folder, file, output, error, cancellationToken).ConfigureAwait(false);

        Revoking identified;
        try
        {
            var text = words.Length == 1 && File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : thing;
            identified = Identify(text, byKey);
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, exception.Message, 2).ConfigureAwait(false);
        }
        if (store.Revocations.Entries.Contains(identified.Revocation))
        {
            await output.WriteLineAsync($"That's revoked already: {identified.What}.").ConfigureAwait(false);
            return 0;
        }
        // A grant named by its ID is looked up, so its revocation says until when it's needed (Q305).
        if (identified.Revocation.Kind == RevocationKind.Grant && store.Grants.FirstOrDefault(g => g.Terms.Id.ToString() == identified.Revocation.Value) is { } recorded)
            identified = Revoking.Grant(recorded);
        folder.Revoke(identified);
        await output.WriteLineAsync($"Revoked {identified.What}. A running daemon stops honouring it at its next gate.").ConfigureAwait(false);
        return 0;
    }

    // meep trust issue <key.pub> [options]: a credential for the key, signed by this machine's authority with ssh-keygen -s.
    public static async Task<int> IssueAsync(string[] words, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (IssueRequest.Parse(words) is not { } request)
            return await FailAsync(error, IssueUsage, 2).ConfigureAwait(false);
        List<PermissionName> permissions;
        try
        {
            permissions = [.. request.Permissions.Select(PermissionName.Parse)];
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, exception.Message, 2).ConfigureAwait(false);
        }
        var folder = TrustFolder.In(options.Home);
        if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
            return 1;
        if (!store.IsSetUp)
            return await FailAsync(error, "There's no authority on this machine to issue credentials with yet. Make one with: meep trust init", 1).ConfigureAwait(false);

        string subject;
        try
        {
            subject = await ReadPublicKeyAsync(request.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, exception.Message, 2).ConfigureAwait(false);
        }
        var target = request.OutputFile();
        if (File.Exists(target))
            return await FailAsync(error, $"There's a file {target} already. Name another with --out, or remove it first.", 1).ConfigureAwait(false);

        ulong serial;
        try
        {
            serial = folder.NextSerial();
        }
        catch (IOException exception)
        {
            return await FailAsync(error, $"Couldn't take the next serial from {folder.Serial}, perhaps because another meep trust issue is running. Try again. ({exception.Message})", 1).ConfigureAwait(false);
        }
        var work = PrivateFolders.Create(Path.Combine(folder.Path, $"issuing-{Guid.NewGuid():N}"));
        try
        {
            var key = Path.Combine(work, "subject.pub");
            await File.WriteAllTextAsync(key, subject, cancellationToken).ConfigureAwait(false);
            var issued = await SshKeygenAsync(request.SshKeygenArguments(folder.AuthorityKey, serial, permissions, key), cancellationToken).ConfigureAwait(false);
            var certificate = Path.Combine(work, "subject-cert.pub");
            if (!issued.Succeeded || !File.Exists(certificate))
                return await FailAsync(error, $"ssh-keygen couldn't issue the credential: {issued.Said}", 1).ConfigureAwait(false);
            var credential = Credential.Parse(await File.ReadAllTextAsync(certificate, cancellationToken).ConfigureAwait(false));
            await File.WriteAllTextAsync(target, credential.ToText() + $" {credential.KeyId}\n", cancellationToken).ConfigureAwait(false);
            await ReportIssuedAsync(credential, target, store, output).ConfigureAwait(false);
            return 0;
        }
        catch (Win32Exception)
        {
            return await FailAsync(error, NoSshKeygen, 1).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, $"ssh-keygen made a credential Meep can't read: {exception.Message}", 1).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // meep trust tidy: takes out of grants and revoked what's no longer needed (Q305).
    public static async Task<int> TidyAsync(PluginOptions options, TextWriter output, TextWriter error)
    {
        var folder = TrustFolder.In(options.Home);
        Tidied tidied;
        try
        {
            tidied = folder.Tidy(DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is TrustStoreException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        if (tidied.Grants.Count + tidied.Revocations.Count == 0)
        {
            await output.WriteLineAsync("Nothing in the trust store needed tidying.").ConfigureAwait(false);
            return 0;
        }
        foreach (var (what, removed) in new[] { ("grants", tidied.Grants), ("revoked", tidied.Revocations) }.Where(part => part.Item2.Count > 0))
        {
            await output.WriteLineAsync($"Took {removed.Count} {(removed.Count == 1 ? "line" : "lines")} out of {what}:").ConfigureAwait(false);
            foreach (var line in removed)
                await output.WriteLineAsync($"  {line}").ConfigureAwait(false);
        }
        return 0;
    }

    // meep trust list: everything in the store.
    public static async Task<int> ListAsync(PluginOptions options, TextWriter output, TextWriter error)
    {
        var folder = TrustFolder.In(options.Home);
        if (await LoadAsync(folder, error).ConfigureAwait(false) is not { } store)
            return 1;
        if (!store.IsSetUp && store.Authorities.Count == 0 && store.Roots.Count == 0)
        {
            await output.WriteLineAsync($"There's no trust store yet. Set one up with meep trust init. It'll be in {folder.Path}.").ConfigureAwait(false);
            return 0;
        }
        var lines = new List<string> { $"The trust store in {folder.Path}:" };
        lines.Add(store.OwnAuthority is { } own ? $"This machine's authority: {own.Fingerprint}" : "This machine has no authority of its own; make one with meep trust init.");

        lines.Add(Heading("Authorities", store.Authorities.Count));
        var width = store.Authorities.Select(key => key.Name.Length).DefaultIfEmpty(0).Max();
        lines.AddRange(store.Authorities.Select(key =>
            $"  {key.Name.PadRight(width)}  {key.Fingerprint}  {key.Type}  {string.Join(", ", key.Namespaces ?? ["any namespace"])}"));

        lines.Add(Heading("Roots", store.Roots.Count));
        lines.AddRange(store.Roots.Select(root =>
            $"  {root.Name}  {root.Certificate.Subject}  until {root.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}"));

        lines.Add(Heading("Revoked", store.Revocations.Entries.Count + store.Revocations.Lists.Count));
        lines.AddRange(store.Revocations.Entries.Select(entry => $"  {entry}"));
        var outOfDate = store.Revocations.OutOfDateAt(DateTime.UtcNow);
        lines.AddRange(store.Revocations.Lists.Select(list => outOfDate.TryGetValue(list, out var due)
            ? $"  the list {list}, out of date since {due:yyyy-MM-dd HH:mm}Z: certificates from its issuer will fail until it's replaced"
            : $"  the list {list}"));

        lines.Add(Heading("Grants", store.Grants.Count));
        lines.AddRange(store.Grants.Select(grant =>
            $"  {grant.Terms.Id}  {grant.Terms.Permission}  for {grant.Terms.CredentialKey} serial {grant.Terms.CredentialSerial}"));

        lines.Add(Heading("Permissions", store.Permissions.All.Count));
        lines.AddRange(store.Permissions.All.Select(permission => $"  {permission.ToDirective()}"));
        if (store.Problems.Count > 0)
        {
            lines.Add("Problems:");
            lines.AddRange(store.Problems.Select(problem => $"  {problem}"));
        }
        foreach (var line in lines)
            await output.WriteLineAsync(line).ConfigureAwait(false);
        return 0;

        static string Heading(string what, int count) => count == 0 ? $"{what}: none" : $"{what}:";
    }

    private const string NoSshKeygen = "meep trust uses ssh-keygen, which comes with OpenSSH. Install it, or put it on your PATH.";

    // A revocation for a credential, key, fingerprint, grant ID or certificate.
    private static Revoking Identify(string text, bool byKey)
    {
        var trimmed = text.Trim();
        if (Guid.TryParse(trimmed, out var grant))
            return Revoking.Grant(grant);
        if (trimmed.StartsWith("SHA256:", StringComparison.Ordinal) && !trimmed.Contains(' ', StringComparison.Ordinal))
            return Revoking.Key(trimmed);
        if (trimmed.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            return Revoking.Certificate(Certificate.FromPem(trimmed));
        if (trimmed.Contains("-cert-v01@openssh.com", StringComparison.Ordinal))
        {
            var credential = Credential.Parse(trimmed);
            return byKey ? Revoking.KeyOf(credential) : Revoking.Credential(credential);
        }
        if (trimmed.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new FormatException("That's a private key. Revoke the public one beside it, its .pub file.");
        return Revoking.Key(PublicKey.Parse("key", trimmed).Fingerprint);
    }

    private static async Task<int> AddRevocationListAsync(TrustFolder folder, string file, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var name = FileName(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file);
        try
        {
            var what = Revocations.DescribeList(name, bytes);
            var written = folder.AddRevocationList(name, bytes);
            await output.WriteLineAsync($"Added the {what}, as {written}. Copy a newer one over it to update it.").ConfigureAwait(false);
            return 0;
        }
        catch (FormatException exception)
        {
            return await FailAsync(error, exception.Message, 1).ConfigureAwait(false);
        }
    }

    private static async Task ReportIssuedAsync(Credential credential, string file, TrustStore store, TextWriter output)
    {
        var until = credential.ValidBefore is { } before ? $"until {before:yyyy-MM-dd HH:mm}Z" : "with no end";
        var permissions = credential.Permissions.Count == 0 ? "no permissions" : string.Join(", ", credential.Permissions);
        await output.WriteLineAsync($"Issued {credential.KeyId} a credential, serial {credential.Serial}, {until}, with {permissions}, in {file}.").ConfigureAwait(false);
        foreach (var undefined in credential.Permissions.Where(p => store.Permissions.Find(p.Name) is null).Select(p => p.Name).Distinct())
            await output.WriteLineAsync($"{undefined} isn't defined in {store.Folder.Permissions} yet, so it can't be met until it is.").ConfigureAwait(false);
        await output.WriteLineAsync($"See what's in it with: ssh-keygen -L -f {file}").ConfigureAwait(false);
    }

    // The trust store, or null having said why it can't be read.
    private static async Task<TrustStore?> LoadAsync(TrustFolder folder, TextWriter error)
    {
        try
        {
            return TrustStore.Load(folder);
        }
        catch (TrustStoreException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<string> ReadPublicKeyAsync(string file, CancellationToken cancellationToken)
    {
        file = HomeFolder.Expand(file);
        if (!File.Exists(file))
            throw new FormatException($"Can't find {file}.");
        var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new FormatException($"{file} is a private key. Use the public one beside it, {file}.pub.");
        return text;
    }

    private static Task<ProgramResult> SshKeygenAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        ProgramRunner.RunAsync("ssh-keygen", arguments, cancellationToken: cancellationToken);

    private static async Task<int> FailAsync(TextWriter error, string message, int code)
    {
        await error.WriteLineAsync(message).ConfigureAwait(false);
        return code;
    }

    // The machine's name, as an authority's name: anything but letters, digits, ., - and _ made a -.
    private static string MachineName() => FileName(Environment.MachineName);

    private static string FileName(string text)
    {
        var name = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')]).Trim('.');
        return name.Length == 0 ? "root" : name;
    }
}

/// <summary>What <c>meep trust issue</c> was asked for.</summary>
/// <param name="Key">The subject's public key file.</param>
/// <param name="Id">The key ID to give it, or null for the key's comment or file name.</param>
/// <param name="Principals">The names it's valid for, comma-separated, or null for any.</param>
/// <param name="Valid">How long it's valid, as <c>ssh-keygen -V</c> takes it, or <c>always</c>.</param>
/// <param name="Permissions">The permissions it carries.</param>
/// <param name="Bearer">Whether it may be used without proof of holding its key.</param>
/// <param name="Out">Where to write it, or null for beside the key.</param>
internal sealed record IssueRequest(string Key, string? Id, string? Principals, string Valid, IReadOnlyList<string> Permissions, bool Bearer, string? Out)
{
    /// <summary>How long a credential is valid if <c>--valid</c> doesn't say.</summary>
    public const string DefaultValidity = "+52w";

    /// <summary>Reads the words after <c>meep trust issue</c>, or null if they aren't right.</summary>
    public static IssueRequest? Parse(string[] words)
    {
        string? key = null, id = null, principals = null, valid = null, output = null;
        var permissions = new List<string>();
        var bearer = false;
        for (var i = 0; i < words.Length; i++)
        {
            string? Next() => i + 1 < words.Length ? words[++i] : null;
            switch (words[i])
            {
                case "--id": id = Next() ?? ""; break;
                case "--principals": principals = Next() ?? ""; break;
                case "--valid": valid = Next() ?? ""; break;
                case "--out": output = Next() ?? ""; break;
                case "--permission": permissions.Add(Next() ?? ""); break;
                case "--bearer": bearer = true; break;
                case var word when !word.StartsWith("--", StringComparison.Ordinal) && key is null: key = word; break;
                default: return null;
            }
        }
        if (key is null || new[] { id, principals, valid, output }.Any(value => value is "") || permissions.Contains(""))
            return null;
        return new IssueRequest(key, id, principals, valid ?? DefaultValidity, permissions, bearer, output);
    }

    /// <summary>Where the credential goes: <see cref="Out"/>, or beside the key as <c>name.credential</c>.</summary>
    public string OutputFile()
    {
        if (Out is not null)
            return Path.GetFullPath(HomeFolder.Expand(Out));
        var key = Path.GetFullPath(HomeFolder.Expand(Key));
        var stem = key.EndsWith(".pub", StringComparison.Ordinal) ? key[..^4] : key;
        return stem + ".credential";
    }

    /// <summary>
    /// The arguments for <c>ssh-keygen -s</c>, signing <paramref name="subject"/> with <paramref name="authority"/>:
    /// <c>-O clear</c> leaves out SSH's own extensions, so it carries only Meep's.
    /// </summary>
    public List<string> SshKeygenArguments(string authority, ulong serial, IEnumerable<PermissionName> permissions, string subject)
    {
        var arguments = new List<string> { "-q", "-s", authority, "-I", Id ?? DefaultId(), "-z", $"{serial}", "-O", "clear" };
        if (Principals is not null)
            arguments.AddRange(["-n", Principals]);
        if (Valid != "always")
            arguments.AddRange(["-V", Valid]);
        foreach (var permission in permissions)
        {
            arguments.AddRange(["-O", permission.Scope is null
                ? $"extension:{permission.Name}{Credential.ExtensionSuffix}"
                : $"extension:{permission.Name}{Credential.ExtensionSuffix}={permission.Scope.Pattern}"]);
        }
        if (Bearer)
            arguments.AddRange(["-O", $"extension:{Credential.PermitBearerExtension}"]);
        arguments.Add(subject);
        return arguments;
    }

    // The key's comment, as in alice@laptop, or else its file's name.
    private string DefaultId()
    {
        var file = HomeFolder.Expand(Key);
        var parts = File.ReadAllText(file).Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? parts[2] : Path.GetFileNameWithoutExtension(file);
    }
}
