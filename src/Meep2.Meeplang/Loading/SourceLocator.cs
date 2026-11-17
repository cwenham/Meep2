using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Finds where a class is written from its assembly's portable PDB: the one beside it, or one embedded in it. The
/// PDB gives the file and lines of each method's code; the class's own line is found by reading back from its first
/// method in the file, when the file is on this machine. SourceLink in the PDB, if it has it, gives an address the
/// file can be read from anywhere.
/// </summary>
internal static class SourceLocator
{
    private static readonly Guid s_sourceLink = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    // By assembly, without keeping a plugin's assembly from being unloaded.
    private static readonly ConditionalWeakTable<Assembly, Symbols> s_symbols = new();
    private static readonly Symbols s_none = new(null);

    /// <summary>Where a class is written, or null if its assembly has no symbols to say.</summary>
    public static SourceLocation? Find(Type type)
    {
        try
        {
            return s_symbols.GetValue(type.Assembly, assembly => Open(assembly) ?? s_none).Find(type);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Symbols? Open(Assembly assembly)
    {
        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location))
            return null;
        try
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var embedded = pe.ReadDebugDirectory().FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            if (embedded.DataSize > 0)
                return new Symbols(pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded));
            var beside = Path.ChangeExtension(assembly.Location, ".pdb");
            if (!File.Exists(beside))
                return null;
            return new Symbols(MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(beside)));
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Symbols(MetadataReaderProvider? provider)
    {
        private readonly MetadataReader? _pdb = provider?.GetMetadataReader();
        private readonly Lock _lock = new();
        private IReadOnlyList<(string Local, string Url)>? _links;

        public SourceLocation? Find(Type type)
        {
            if (_pdb is null)
                return null;
            lock (_lock)
            {
                string? file = null;
                var line = int.MaxValue;
                foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)))
                {
                    var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
                    var information = _pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle());
                    foreach (var point in information.GetSequencePoints())
                    {
                        if (point.IsHidden)
                            continue;
                        var document = _pdb.GetString(_pdb.GetDocument(point.Document).Name);
                        // A class may be split over files; keep to the first file met, as its methods mostly are.
                        file ??= document;
                        if (document == file && point.StartLine < line)
                            line = point.StartLine;
                    }
                }
                if (file is null)
                    return null;
                return new SourceLocation(file, Declaration(file, line, type.Name) ?? line, Url(file));
            }
        }

        // The line the class is declared on: the nearest line above its first code that declares it.
        private static int? Declaration(string file, int from, string name)
        {
            if (!File.Exists(file))
                return null;
            var lines = File.ReadAllLines(file);
            var plain = name.Split('`')[0];
            for (var i = Math.Min(from, lines.Length) - 1; i >= 0; i--)
            {
                var text = lines[i];
                foreach (var keyword in (string[])["class ", "record ", "struct "])
                {
                    var at = text.IndexOf(keyword + plain, StringComparison.Ordinal);
                    var after = at + keyword.Length + plain.Length;
                    if (at >= 0 && (after == text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_')))
                        return i + 1;
                }
            }
            return null;
        }

        // The file's address from SourceLink, whose map is local paths (ending in * for a folder) to addresses.
        private string? Url(string file)
        {
            _links ??= Links(_pdb!);
            foreach (var (local, url) in _links)
            {
                if (local.EndsWith('*') && file.StartsWith(local[..^1], StringComparison.Ordinal))
                    return url.Replace("*", file[(local.Length - 1)..].Replace('\\', '/'), StringComparison.Ordinal);
                if (local == file)
                    return url;
            }
            return null;
        }

        private static List<(string Local, string Url)> Links(MetadataReader pdb)
        {
            var links = new List<(string, string)>();
            foreach (var handle in pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
            {
                var information = pdb.GetCustomDebugInformation(handle);
                if (pdb.GetGuid(information.Kind) != s_sourceLink)
                    continue;
                using var json = JsonDocument.Parse(pdb.GetBlobBytes(information.Value));
                if (json.RootElement.TryGetProperty("documents", out var documents))
                {
                    foreach (var document in documents.EnumerateObject())
                        links.Add((document.Name, document.Value.GetString() ?? ""));
                }
            }
            return links;
        }
    }
}
