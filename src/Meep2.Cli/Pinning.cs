using System.Security.Cryptography;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Cli;

/// <summary>
/// <c>meep pin</c> (Q136): pins the files a pipeline includes on their <c>#include</c> lines, those they include first,
/// and says the pipeline's own SHA-256. A pipeline pinned by that SHA, as <c>#sha256=</c> on its URL, then pins
/// everything it includes too.
/// </summary>
internal static class Pinning
{
    /// <summary>Pins each file's includes. Returns the exit code.</summary>
    public static async Task<int> PinAsync(IReadOnlyList<string> files, TextWriter output, TextWriter error)
    {
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file);
            if (!File.Exists(path))
            {
                await error.WriteLineAsync($"Can't find {file}.").ConfigureAwait(false);
                return 1;
            }
            var pinned = 0;
            string sha;
            try
            {
                sha = Pin(path, new Dictionary<string, string>(StringComparer.Ordinal), [], ref pinned);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
                return 1;
            }
            var includes = pinned == 1 ? "1 include" : $"{pinned} includes";
            await output.WriteLineAsync($"Pinned {includes} of {file}. Its SHA-256 is {sha}, so pin it with #sha256={sha} on its URL.").ConfigureAwait(false);
        }
        return 0;
    }

    // Pins what a file includes, and returns the file's SHA-256 as it then is.
    private static string Pin(string path, Dictionary<string, string> done, HashSet<string> within, ref int pinned)
    {
        if (done.TryGetValue(path, out var known))
            return known;
        if (!within.Add(path))
            throw new InvalidOperationException($"{path} includes itself, through the files it includes.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"There's no file {path} to pin.");

        var text = File.ReadAllText(path);
        var syntax = MeeplangParser.Parse(new SourceFile(path, text), _ => ""); // only its own #include lines are needed
        var lines = text.Split('\n');
        foreach (var include in syntax.Includes)
        {
            var sha = Pin(MeeplangParser.IncludePath(include.Path, path), done, within, ref pinned);
            if (include.Sha256 == sha)
                continue;
            lines[include.Span.Line - 1] = WithPin(lines[include.Span.Line - 1], include, sha);
            pinned++;
        }
        var written = string.Join('\n', lines);
        if (written != text)
            File.WriteAllText(path, written);
        within.Remove(path);
        return done[path] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    // The #include line with sha256: and the SHA after its file, in place of any pin it had, keeping what follows.
    private static string WithPin(string line, IncludeDirective include, string sha)
    {
        var quote = line.IndexOfAny(['"', '\''], include.Span.Column - 1);
        var close = line.IndexOf(line[quote], quote + 1);
        var after = line[(close + 1)..];
        var rest = after.TrimStart(' ', '\t');
        if (rest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var end = rest.IndexOfAny([' ', '\t', '\r']);
            after = end < 0 ? "" : rest[end..];
        }
        return $"{line[..(close + 1)]} sha256:{sha}{after}";
    }
}
