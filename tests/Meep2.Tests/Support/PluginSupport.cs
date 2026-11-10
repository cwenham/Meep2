using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using System.Diagnostics;

namespace Meep2.Tests.Support;

/// <summary>
/// A place to try plugins: a Meep folder of its own, a signing key it trusts, and copies of the test plugin as a
/// folder, a zip and a NuGet feed, made from what the build put in plugins/.
/// </summary>
public sealed class PluginLab : IDisposable
{
    private readonly TemporaryFolder _folder = new();

    public PluginLab()
    {
        Key = SshKeyPair.CreateEd25519("tester");
        Options = new PluginOptions { Home = System.IO.Path.Combine(_folder.Path, "home"), Trusted = new TrustedKeys([Key.Public]) };
    }

    /// <summary>The test plugin's build output, as the build left it. Copy it before changing it.</summary>
    public static string Build => System.IO.Path.Combine(AppContext.BaseDirectory, "plugins", "build");

    /// <summary>The folder of NuGet packages the build made: the test plugin and its dependency.</summary>
    public static string Feed => System.IO.Path.Combine(AppContext.BaseDirectory, "plugins", "feed");

    /// <summary>A folder for this test's files.</summary>
    public string Path => _folder.Path;

    /// <summary>An SSH key the lab's options trust.</summary>
    internal SshKeyPair Key { get; }

    /// <summary>Settings with a Meep folder of the lab's own, trusting <see cref="Key"/>.</summary>
    public PluginOptions Options { get; set; }

    /// <summary>A copy of the test plugin's build output, signed with <paramref name="key"/> unless that's null.</summary>
    internal string CopyPlugin(string name, SshKeyPair? key)
    {
        var target = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(Build))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
        if (key is not null)
            PluginSignature.Sign(target, key);
        return target;
    }

    /// <summary>The test plugin as a release asset, Meep2.TestPlugin-1.0.0.zip, signed with the lab's key unless told not to, and its SHA-256.</summary>
    public (byte[] Zip, string Sha256) ReleaseAsset(bool signed = true)
    {
        var folder = CopyPlugin("release", signed ? Key : null);
        var zip = System.IO.Path.Combine(Path, "Meep2.TestPlugin-1.0.0.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        return (File.ReadAllBytes(zip), PluginSignature.Hash(zip));
    }

    /// <summary>A copy of the NuGet feed whose plugin package has a signature beside its assembly.</summary>
    public string SignedFeed()
    {
        var feed = System.IO.Path.Combine(Path, "feed");
        Directory.CreateDirectory(feed);
        foreach (var package in Directory.EnumerateFiles(Feed, "*.nupkg"))
            File.Copy(package, System.IO.Path.Combine(feed, System.IO.Path.GetFileName(package)));

        // Sign the assembly the package carries, as its author would before packing, and put the signature in.
        var lib = System.IO.Path.Combine(Path, "lib");
        Directory.CreateDirectory(lib);
        var plugin = System.IO.Path.Combine(feed, "Meep2.TestPlugin.1.0.0.nupkg");
        using (var archive = ZipFile.Open(plugin, ZipArchiveMode.Update))
        {
            archive.GetEntry("lib/net11.0/Meep2.TestPlugin.dll")!.ExtractToFile(System.IO.Path.Combine(lib, "Meep2.TestPlugin.dll"));
            var signature = PluginSignature.Sign(lib, Key, [System.IO.Path.Combine(lib, "Meep2.TestPlugin.dll")]);
            archive.CreateEntryFromFile(signature, "lib/net11.0/" + PluginSignature.FileName);
            archive.CreateEntryFromFile(System.IO.Path.Combine(lib, PluginSignature.ListName), "lib/net11.0/" + PluginSignature.ListName);
        }
        return feed;
    }

    /// <summary>
    /// Runs a pipeline file through the bootstrap stages, Parse, Install, Build and Host, as meep run does, with the
    /// lab's settings and the catalog given. The file has a name of its own unless <paramref name="file"/> gives one.
    /// </summary>
    public async Task<PluginRunResult> RunAsync(string text, PluginCatalog catalog, HttpClient? http = null, string? file = null)
    {
        var path = System.IO.Path.Combine(Path, file ?? $"pipeline-{Guid.NewGuid():N}.meep");
        await File.WriteAllTextAsync(path, text);

        var output = new SharedText();
        var warnings = new SharedText();
        var shared = TextWriter.Synchronized(output); // one lock for every WriteLine, so lines from parallel branches don't mix
        var errors = new ConcurrentQueue<string>();
        var registry = new ModuleRegistry()
            .AddAssembly(CoreModules.Assembly)
            .Add<WriteLine, WriteLineParameters>("WriteLine", p => new WriteLine(p, shared));
        var hosted = new PipelineOptions { UnhandledError = e => errors.Enqueue(e.Error) };
        var bootstrap = new PipelineOptions
        {
            UnhandledError = e => errors.Enqueue(e is MeeplangErrorMessage meeplang ? string.Join("\n", meeplang.Diagnostics) : e.Error),
        };

        var builder = new PipelineBuilder("bootstrap", bootstrap);
        builder.Chain(
            builder.AddSource(() => new Read(new ReadParameters { Path = path })),
            builder.Add(() => new Parse()),
            builder.Add(() => new Install(registry, new PluginInstaller(Options, http), catalog)),
            builder.Add(() => new Build(registry, hosted)),
            builder.Add(() => new Host(TextWriter.Synchronized(warnings))));
        await using (var pipeline = builder.Build())
            await Wait.For(pipeline.RunAsync());
        return new PluginRunResult(output.ToString().ReplaceLineEndings("\n"), warnings.ToString(), [.. errors]);
    }

    /// <summary>The first loaded plugin's context, weakly, without keeping anything of the plugin's alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference? Loaded(PluginCatalog catalog) => catalog.Loaded is [var plugin, ..] ? plugin.Unloaded : null;

    public void Dispose()
    {
        Key.Dispose();
        _folder.Dispose();
    }
}

/// <summary>Runs OpenSSH's ssh-keygen, for checking Meep's SSH signatures against the real thing.</summary>
public static class SshKeygen
{
    private static readonly Lazy<bool> s_available = new(() =>
    {
        try
        {
            return Run(["-?"]).GetAwaiter().GetResult().Code is 0 or 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Whether ssh-keygen is on the PATH.</summary>
    public static bool Available => s_available.Value;

    /// <summary>Makes a key pair with no passphrase: <c>ssh-keygen -t type -b bits</c>. Returns the private key's path.</summary>
    public static async Task<string> NewKeyAsync(string folder, string type, int? bits = null)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"id_{type}{bits}");
        string[] arguments = bits is null ? ["-q", "-t", type, "-N", "", "-f", path] : ["-q", "-t", type, "-b", $"{bits}", "-N", "", "-f", path];
        var (code, output) = await Run(arguments);
        return code == 0 ? path : throw new InvalidOperationException($"ssh-keygen couldn't make a key: {output}");
    }

    /// <summary>Runs ssh-keygen, giving it <paramref name="input"/> on standard input. Returns its exit code and what it said.</summary>
    public static async Task<(int Code, string Output)> Run(string[] arguments, string? input = null)
    {
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input ?? "");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + await error);
    }
}

/// <summary>What a pipeline run in a <see cref="PluginLab"/> wrote, warned and failed with.</summary>
public sealed record PluginRunResult(string Output, string Warnings, IReadOnlyList<string> Errors)
{
    public string AllErrors => string.Join("\n", Errors);
}
