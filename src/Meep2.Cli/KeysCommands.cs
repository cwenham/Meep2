using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary>meep keys and meep sign: trusting people's SSH keys, and signing plugins with your own (Q36, Q101).</summary>
internal static class KeysCommands
{
    public const string TrustUsage = "Trust a key as: meep keys trust <name> ssh-ed25519 AAAA…, meep keys trust <name> <file.pub>, or meep keys trust github:<user>.";

    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meep2/1.0");
        return client;
    });

    // meep keys trust <name> <type> <key> | <name> <file.pub> | github:<user>: adds keys to the allowed-signers file.
    public static async Task<int> TrustAsync(string[] words, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        List<PublicKey> keys;
        try
        {
            keys = words switch
            {
                [var github] when github.StartsWith("github:", StringComparison.OrdinalIgnoreCase) =>
                    await GitHubKeysAsync(github["github:".Length..], options, output, cancellationToken).ConfigureAwait(false),
                [var name, var file] => [PublicKey.Parse(name, await ReadPublicKeyAsync(file, cancellationToken).ConfigureAwait(false))],
                [var name, var type, var key, ..] => [PublicKey.Parse(name, $"{type} {key}")],
                _ => throw new FormatException(TrustUsage),
            };
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is PluginException or HttpRequestException or IOException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        TrustedKeys trusted;
        try
        {
            trusted = TrustedKeys.Load(options.TrustedKeysFile);
        }
        catch (PluginException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        foreach (var key in keys)
        {
            if (trusted.Keys.FirstOrDefault(key.SameKeyAs) is { } already)
            {
                await output.WriteLineAsync($"You trust {key.Fingerprint} already, as {already.Name}.").ConfigureAwait(false);
                continue;
            }
            await AppendAsync(options.TrustedKeysFile, key.ToString(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Plugins signed by {key.Name}'s {key.Type} key {key.Fingerprint} can load now.").ConfigureAwait(false);
        }
        return 0;
    }

    // meep keys list: the keys in the allowed-signers file.
    public static async Task<int> ListAsync(PluginOptions options, TextWriter output, TextWriter error)
    {
        TrustedKeys trusted;
        try
        {
            trusted = TrustedKeys.Load(options.TrustedKeysFile);
        }
        catch (PluginException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        if (trusted.Keys.Count == 0)
        {
            await output.WriteLineAsync($"You don't trust any keys yet. Add one with meep keys trust. They're kept in {options.TrustedKeysFile}.").ConfigureAwait(false);
            return 0;
        }
        var width = trusted.Keys.Max(key => key.Name.Length);
        foreach (var key in trusted.Keys)
            await output.WriteLineAsync($"{key.Name.PadRight(width)}  {key.Fingerprint}  {key.Type}").ConfigureAwait(false);
        return 0;
    }

    // meep sign <folder or files> [--key <private key>]: lists the files and signs the list with ssh-keygen.
    public static async Task<int> SignAsync(string[] paths, string? keyPath, PluginOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        keyPath = keyPath is null ? DefaultKey() : Expand(keyPath);
        if (keyPath is null)
        {
            await error.WriteLineAsync("meep sign needs your SSH key, as in --key ~/.ssh/id_ed25519. Make one with: ssh-keygen -t ed25519").ConfigureAwait(false);
            return 2;
        }
        if (!File.Exists(keyPath))
        {
            await error.WriteLineAsync($"Can't find the key {keyPath}.").ConfigureAwait(false);
            return 1;
        }

        // Pipeline files are signed one by one, each with a .sig beside it (goal 7).
        if (paths.Length > 0 && paths.All(path => path.EndsWith(".meep", StringComparison.OrdinalIgnoreCase)))
            return await SignPipelinesAsync(paths, keyPath, output, error, cancellationToken).ConfigureAwait(false);

        string folder;
        IEnumerable<string>? files = null;
        if (paths is [var only] && Directory.Exists(only))
        {
            folder = only;
        }
        else
        {
            var missing = paths.FirstOrDefault(path => !File.Exists(path));
            if (missing is not null)
            {
                await error.WriteLineAsync($"Can't find {missing}.").ConfigureAwait(false);
                return 1;
            }
            var folders = paths.Select(path => Path.GetDirectoryName(Path.GetFullPath(path))!).Distinct().ToList();
            if (folders.Count > 1)
            {
                await error.WriteLineAsync("Files to sign together have to be in one folder, where the signature goes.").ConfigureAwait(false);
                return 2;
            }
            folder = folders[0];
            files = paths;
        }

        SshSignature signature;
        string list;
        try
        {
            list = PluginSignature.WriteList(folder, files);
            var signaturePath = Path.Combine(folder, PluginSignature.FileName);
            File.Delete(signaturePath);
            var (code, said) = await SshKeygenAsync(["-Y", "sign", "-n", PluginSignature.Namespace, "-f", keyPath, list], cancellationToken).ConfigureAwait(false);
            if (code != 0 || !File.Exists(signaturePath))
            {
                await error.WriteLineAsync($"ssh-keygen couldn't sign {list}: {said.Trim()}").ConfigureAwait(false);
                return 1;
            }
            signature = PluginSignature.Read(folder)!;
        }
        catch (Win32Exception)
        {
            await error.WriteLineAsync("meep sign uses ssh-keygen, which comes with OpenSSH. Install it, or put it on your PATH.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        var count = File.ReadLines(list).Count(line => line.Length > 0);
        var key = signature.Key;
        await output.WriteLineAsync($"Signed {count} file{(count == 1 ? "" : "s")} with the {key.Type} key {key.Fingerprint}, in {Path.Combine(folder, PluginSignature.FileName)}.").ConfigureAwait(false);
        var trust = $"meep keys trust {Environment.UserName} {key.Text}";
        if (TrustedKeys.Load(options.TrustedKeysFile).Find(key) is null)
            await output.WriteLineAsync($"You don't trust this key yet, so meep won't load the plugin here until you do: {trust}").ConfigureAwait(false);
        await output.WriteLineAsync($"People using the plugin can trust your key with {trust}, or with meep keys trust github:<you> if it's on your GitHub account.").ConfigureAwait(false);
        return 0;
    }

    // The keys on a GitHub account, from github.com/<user>.keys.
    private static async Task<List<PublicKey>> GitHubKeysAsync(string user, PluginOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        if (user.Length == 0 || !user.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new FormatException($"A GitHub user's name has letters, digits and -, not \"{user}\".");
        using var response = await s_http.Value.GetAsync(new Uri(options.GitHub, $"{user}.keys"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PluginException($"GitHub has no user called {user}.");
        response.EnsureSuccessStatusCode();
        var keys = new List<PublicKey>();
        foreach (var line in (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                keys.Add(PublicKey.Parse($"github:{user}", line));
            }
            catch (FormatException exception)
            {
                await output.WriteLineAsync($"Skipped one of {user}'s keys: {exception.Message}").ConfigureAwait(false);
            }
        }
        return keys.Count > 0 ? keys : throw new PluginException($"{user} has no SSH keys on GitHub that can sign plugins.");
    }

    private static async Task<string> ReadPublicKeyAsync(string file, CancellationToken cancellationToken)
    {
        file = Expand(file);
        if (!File.Exists(file))
            throw new FormatException($"Can't find {file}. {TrustUsage}");
        var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new FormatException($"{file} is a private key. Trust the public one beside it, {file}.pub, which is the one to share.");
        return text;
    }

    // ~/.ssh's usual keys, the first that's there.
    private static async Task<int> SignPipelinesAsync(string[] paths, string keyPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                await error.WriteLineAsync($"Can't find {path}.").ConfigureAwait(false);
                return 1;
            }
            var signature = PipelineSignature.FileFor(Path.GetFullPath(path));
            File.Delete(signature);
            try
            {
                var (code, said) = await SshKeygenAsync(["-Y", "sign", "-n", PipelineSignature.Namespace, "-f", keyPath, Path.GetFullPath(path)], cancellationToken).ConfigureAwait(false);
                if (code != 0 || !File.Exists(signature))
                {
                    await error.WriteLineAsync($"ssh-keygen couldn't sign {path}: {said.Trim()}").ConfigureAwait(false);
                    return 1;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                await error.WriteLineAsync("meep sign uses ssh-keygen, which comes with OpenSSH. Install it, or put it on your PATH.").ConfigureAwait(false);
                return 1;
            }
            await output.WriteLineAsync($"Signed {path} as {signature}. Publish them together; a pipeline from a URL or Git needs its signature beside it.").ConfigureAwait(false);
        }
        return 0;
    }

    private static string? DefaultKey()
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(name => Path.Combine(ssh, name)).FirstOrDefault(File.Exists);
    }

    private static string Expand(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static async Task<(int Code, string Error)> SshKeygenAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await standardError.ConfigureAwait(false) + await standardOutput.ConfigureAwait(false));
    }

    private static async Task AppendAsync(string path, string line, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }
}
