using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Meep2.Meeplang.Compilation;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Shared;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Describes every verb and inflection a registry has, the core modules' and its plugins', for tools that show them:
/// <c>meep lsp</c>'s <c>meep/verbs</c>.
/// </summary>
public static class VerbCatalogue
{
    /// <summary>Every inflection the registry has, by verb then inflection, plugins' after Meep's own.</summary>
    public static IReadOnlyList<VerbEntry> Describe(ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var all = new List<ModuleRegistration>();
        var seen = new HashSet<ModuleRegistration>(ReferenceEqualityComparer.Instance);
        foreach (var verb in registry.Verbs)
        {
            if (registry.TryGetVerb(verb, out var inflections))
                all.AddRange(inflections.Where(seen.Add));
        }
        foreach (var plugin in registry.Plugins)
        {
            foreach (var verb in registry.PluginVerbs(plugin))
            {
                if (registry.TryGetPluginVerb(plugin, verb, out var inflections))
                    all.AddRange(inflections.Where(seen.Add));
            }
        }
        return Describe(all);
    }

    /// <summary>Describes these inflections, as <see cref="Describe(ModuleRegistry)"/> does every one a registry has.</summary>
    public static IReadOnlyList<VerbEntry> Describe(IEnumerable<ModuleRegistration> inflections)
    {
        ArgumentNullException.ThrowIfNull(inflections);
        var docs = new Documentation();
        return [.. inflections
            .OrderBy(r => r.Plugin is not null)
            .ThenBy(r => r.Plugin, StringComparer.Ordinal)
            .ThenBy(r => r.Verb, StringComparer.Ordinal)
            .ThenBy(r => r.Inflection, StringComparer.Ordinal)
            .Select(r => Entry(r, docs))];
    }

    private static VerbEntry Entry(ModuleRegistration registration, Documentation docs)
    {
        var type = registration.ModuleType;
        var whenOf = type.GetCustomAttributes<EmitsAttribute>().ToDictionary(a => a.MessageType, a => a.When);
        return new VerbEntry(
            registration.Verb,
            registration.Inflection,
            registration.Role.ToString().ToLowerInvariant(),
            registration.IsSource ? "source" : registration.IsPassthrough ? "passthrough" : "inflection",
            Parameters(registration.ParametersType, docs),
            registration.IsSource ? [] : [NameOf(registration.InputType ?? typeof(Message))],
            [.. registration.Emits.Select(t => new EmittedType(NameOf(t), whenOf.GetValueOrDefault(t) is { } when ? Kebab(when) : null))])
        {
            Plugin = registration.Plugin,
            RoleDeclared = registration.RoleDeclared,
            EmitsInput = registration.EmitsInput || registration.IsPassthrough,
            Reconfigurable = registration.Reconfigure is not null,
            Requires = [.. registration.RequiredPermissions.Select(p => p.ToString())],
            Description = docs.Summary(type, "T:" + type.FullName),
            Class = type.FullName,
            Source = SourceLocator.Find(type),
        };
    }

    private static List<ParameterEntry> Parameters(Type? type, Documentation docs)
    {
        if (type is null)
            return [];
        var defaults = Defaults(type);
        var parameters = new List<ParameterEntry>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var positional = property.IsDefined(typeof(PositionalAttribute), false);
            var required = property.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false);
            var (word, perMessage, choices) = Describe(property.PropertyType);
            var value = required || defaults is null ? null : Written(property.GetValue(defaults));
            parameters.Add(new ParameterEntry(Kebab(property.Name), word, positional, required)
            {
                PerMessage = perMessage,
                Default = value,
                Choices = choices,
                // An inherited parameter is documented on the record that declares it.
                Description = docs.Summary(property.DeclaringType ?? type, $"P:{(property.DeclaringType ?? type).FullName}.{property.Name}"),
            });
        }
        return [.. parameters.OrderByDescending(p => p.Positional)];
    }

    // A parameters record as it is before a use fills it in, for the values its properties start with.
    private static object? Defaults(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
            return null;
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMethodException or MemberAccessException)
        {
            return null;
        }
    }

    // What a parameter of a type takes, in Meeplang's words.
    private static (string Word, bool PerMessage, IReadOnlyList<string>? Choices) Describe(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Computed<>))
            return Describe(type.GetGenericArguments()[0]) with { PerMessage = true };
        if (type.IsEnum)
            return ("choice", false, [.. Enum.GetNames(type).Select(n => n.ToLowerInvariant())]);
        var word = type switch
        {
            _ when type == typeof(string) => "text",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(TimeSpan) => "duration",
            _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "time",
            _ when type == typeof(Book) => "book",
            _ when type == typeof(ModuleReference) => "module",
            _ when type == typeof(object) => "any",
            _ when type == typeof(ReadOnlyMemory<byte>) || type == typeof(byte[]) => "bytes",
            _ when type.IsPrimitive || type == typeof(decimal) || type.Name == "BigDecimal" => "number",
            _ when type.Name == "JsonPath" => "path",
            _ when type.IsGenericType && type.GetGenericArguments().Length == 2 && typeof(IEnumerable).IsAssignableFrom(type) => "map of " + Describe(type.GetGenericArguments()[1]).Word,
            _ when type.IsArray => "list of " + Describe(type.GetElementType()!).Word,
            _ when type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) => "list of " + Describe(type.GetGenericArguments()[0]).Word,
            _ => type.Name,
        };
        return (word, false, null);
    }

    // A default value as Meeplang would write it.
    private static string? Written(object? value) => value switch
    {
        null => null,
        string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        bool flag => flag ? "true" : "false",
        TimeSpan span => DurationText.Write(span),
        Enum choice => choice.ToString().ToLowerInvariant(),
        IFormattable number when value.GetType().IsPrimitive || value is decimal => number.ToString(null, CultureInfo.InvariantCulture),
        ICollection { Count: 0 } => null,
        _ when value.GetType().IsValueType && Equals(value, Activator.CreateInstance(value.GetType())) => null,
        _ => value.ToString(),
    };

    private static string NameOf(Type type) => ExpressionBinder.FriendlyName(type);

    private static string Kebab(string name) => ParameterBinder.Kebab(name);

    // The XML documentation shipped beside an assembly, read once each, with the summaries as plain text.
    private sealed class Documentation
    {
        private readonly Dictionary<Assembly, Dictionary<string, string>?> _byAssembly = [];

        public string? Summary(Type type, string member)
        {
            if (!_byAssembly.TryGetValue(type.Assembly, out var members))
                _byAssembly[type.Assembly] = members = Read(type.Assembly);
            return members?.GetValueOrDefault(member);
        }

        private static Dictionary<string, string>? Read(Assembly assembly)
        {
            try
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    return null;
                var path = Path.ChangeExtension(assembly.Location, ".xml");
                if (!File.Exists(path))
                    return null;
                var members = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in XDocument.Load(path).Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is { } name && member.Element("summary") is { } summary)
                        members[name] = Plain(summary);
                }
                return members;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }

        // A summary's text, with <see cref="T:A.B"/> as B and <c>x</c> as x, on one line.
        private static string Plain(XElement element)
        {
            var text = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText plain:
                        text.Append(plain.Value);
                        break;
                    case XElement { Name.LocalName: "see" or "seealso" } see:
                        var target = see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? see.Attribute("href")?.Value ?? "";
                        text.Append(see.Value.Length > 0 ? see.Value : target[(target.LastIndexOfAny(['.', ':']) + 1)..]);
                        break;
                    case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                        text.Append(reference.Attribute("name")?.Value);
                        break;
                    case XElement inner:
                        text.Append(Plain(inner));
                        break;
                }
            }
            return string.Join(' ', text.ToString().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
