using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Meep2.Meeplang.Compilation;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// The verbs a pipeline can use, with their inflections, and the message types <c>is</c> can name. Register module
/// classes with a <see cref="VerbAttribute"/>, or add modules by hand.
/// </summary>
/// <remarks>
/// A plugin's verbs and message types (<see cref="AddPlugin"/>) join the others, and can also be reached by the
/// plugin's name, as in <c>Transit.Departures</c> (Q105). Where a plugin uses a name the core modules have, the core
/// one keeps it. Where two plugins use one, it's only reachable by the plugin's name.
/// </remarks>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, List<ModuleRegistration>> _verbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _messageTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.Ordinal);

    // The library's books, read when a pipeline names one, and the names two plugins' books share. Book names are
    // written as text, some with spaces, so they're matched ignoring case (Q166).
    private readonly Dictionary<string, LibraryEntry> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sharedBooks = new(StringComparer.OrdinalIgnoreCase);

    // Names two plugins both use, so they need the plugin's name: Verb, Verb.Inflection, or a message type's.
    private readonly Dictionary<string, List<string>> _shared = new(StringComparer.Ordinal);

    /// <summary>Starts with no verbs, and the message types <c>Message</c> and <c>ErrorMessage</c>.</summary>
    public ModuleRegistry()
    {
        AddMessageType(typeof(Message));
        AddMessageType(typeof(ErrorMessage));
    }

    /// <summary>A copy to add to, such as one pipeline's plugins, leaving this registry as it is.</summary>
    public ModuleRegistry Clone()
    {
        var copy = new ModuleRegistry();
        foreach (var (verb, inflections) in _verbs)
            copy._verbs[verb] = [.. inflections];
        foreach (var (name, type) in _messageTypes)
            copy._messageTypes[name] = type;
        foreach (var (name, plugin) in _plugins)
            copy._plugins[name] = plugin;
        foreach (var (name, plugins) in _shared)
            copy._shared[name] = [.. plugins];
        foreach (var (name, book) in _books)
            copy._books[name] = book;
        foreach (var (name, plugins) in _sharedBooks)
            copy._sharedBooks[name] = [.. plugins];
        return copy;
    }

    /// <summary>The plugins added, by name.</summary>
    public IEnumerable<string> Plugins => _plugins.Keys;

    /// <summary>Whether a plugin of this name has been added.</summary>
    public bool IsPlugin(string name) => _plugins.ContainsKey(name);

    /// <summary>
    /// Why a plugin can't take this name, or null if it can. A name is a word, not already a plugin's or a verb's,
    /// so that <c>Name.Verb</c> can only mean the plugin's verb.
    /// </summary>
    public string? WhyNotPluginName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return $"A plugin's name is a word of letters and digits, and {name} isn't one.";
        if (_plugins.ContainsKey(name))
            return $"There's already a plugin called {name}.";
        if (_verbs.ContainsKey(name))
            return $"{name} is a verb's name, so it can't be a plugin's too.";
        return null;
    }

    /// <summary>
    /// Registers a plugin's modules and message types, under its own name as well as theirs, so <c>Name.Verb</c> and
    /// <c>is Name.Type</c> reach them (Q105).
    /// </summary>
    /// <exception cref="ArgumentException">The name can't be used (<see cref="WhyNotPluginName"/>), or the plugin's own modules clash with each other.</exception>
    public ModuleRegistry AddPlugin(string name, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (WhyNotPluginName(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var entry = new PluginEntry(name);
        var books = FindBooks(assembly);
        var modules = Registrations(entry, assembly);
        AddOwnVerbs(entry, modules, assembly);
        if (entry.Verbs.ContainsKey(name))
            throw new ArgumentException($"{name} is the name of one of the plugin's own verbs, so it can't be the plugin's too.", nameof(name));
        if (entry.Verbs.Keys.FirstOrDefault(_plugins.ContainsKey) is { } taken)
            throw new ArgumentException($"The plugin has a verb called {taken}, which is another plugin's name. Give that plugin another name with as.");
        _plugins[name] = entry;

        JoinVerbs(modules, assembly, name);
        JoinMessageTypes(entry);
        JoinBooks(entry, books);
        return this;
    }

    // The plugin's modules, and its message types, which go into its entry.
    private static List<ModuleRegistration> Registrations(PluginEntry entry, Assembly assembly)
    {
        var modules = new List<ModuleRegistration>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract)
                continue;
            if (type.GetCustomAttribute<VerbAttribute>() is not null && typeof(AModule).IsAssignableFrom(type))
                modules.AddRange(Registrations(type, entry.Name));
            else if (typeof(Message).IsAssignableFrom(type))
                entry.MessageTypes[ExpressionBinder.FriendlyName(type)] = type;
        }
        return modules;
    }

    // The plugin's own verbs have to agree with each other, as any verb's inflections do.
    private static void AddOwnVerbs(PluginEntry entry, List<ModuleRegistration> modules, Assembly assembly)
    {
        foreach (var registration in modules)
        {
            if (!entry.Verbs.TryGetValue(registration.Verb, out var own))
                entry.Verbs[registration.Verb] = own = [];
            if (Problem(own, registration) is { } clash)
                throw new ArgumentException(clash);
            own.Add(registration);
            foreach (var emitted in registration.Emits.Where(t => t.Assembly == assembly))
                entry.MessageTypes[ExpressionBinder.FriendlyName(emitted)] = emitted;
        }
    }

    // Then they join everyone's, unless the name is taken.
    private void JoinVerbs(List<ModuleRegistration> modules, Assembly assembly, string name)
    {
        foreach (var registration in modules)
        {
            if (!_verbs.TryGetValue(registration.Verb, out var inflections))
                _verbs[registration.Verb] = inflections = [];
            var existing = inflections.FirstOrDefault(r => r.Inflection == registration.Inflection);
            if (existing is null && Problem(inflections, registration) is null)
                inflections.Add(registration);
            else if (existing?.ModuleType != registration.ModuleType && (existing ?? inflections[0]).Plugin is { } other)
                Share(existing is null ? registration.Verb : $"{registration.Verb}.{registration.Inflection}", other, name);
            foreach (var emitted in registration.Emits.Where(t => t.Assembly != assembly))
                _messageTypes.TryAdd(ExpressionBinder.FriendlyName(emitted), emitted);
        }
    }

    // Its message types, under the plugin's name, and under their own unless another's has it.
    private void JoinMessageTypes(PluginEntry entry)
    {
        var name = entry.Name;
        foreach (var (typeName, type) in entry.MessageTypes)
        {
            _messageTypes[$"{name}.{typeName}"] = type;
            if (!_messageTypes.TryGetValue(typeName, out var existing))
                _messageTypes[typeName] = type;
            else if (existing != type && PluginOf(existing) is { } other)
                Share(typeName, other, name);
        }
    }

    // Its books, under the plugin's name, and under their own unless a core book has it. Two plugins' books
    // with one name can only be reached by the plugin's.
    private void JoinBooks(PluginEntry entry, Dictionary<string, LibraryEntry> books)
    {
        var name = entry.Name;
        foreach (var (bookName, book) in books)
        {
            entry.Books.Add(bookName);
            _books[$"{name}.{bookName}"] = book with { Name = $"{name}.{book.Name}" };
            if (_books.TryAdd(bookName, book))
                continue;
            if (_plugins.Values.FirstOrDefault(p => p != entry && p.Books.Contains(bookName)) is { } owner)
            {
                if (!_sharedBooks.TryGetValue(bookName, out var sharers))
                    _sharedBooks[bookName] = sharers = [owner.Name];
                if (!sharers.Contains(name))
                    sharers.Add(name);
            }
        }
    }

    /// <summary>
    /// The plugins that share a name, if they do: a verb, a verb and inflection, or a message type that two plugins
    /// both have, so only the plugin's name tells them apart.
    /// </summary>
    public IReadOnlyList<string>? SharedBy(string name) => _shared.TryGetValue(name, out var plugins) ? plugins : null;

    /// <summary>Finds a plugin's own inflections of a verb.</summary>
    public bool TryGetPluginVerb(string plugin, string verb, [NotNullWhen(true)] out IReadOnlyList<ModuleRegistration>? inflections)
    {
        inflections = null;
        if (!_plugins.TryGetValue(plugin, out var entry) || !entry.Verbs.TryGetValue(verb, out var list))
            return false;
        inflections = list;
        return true;
    }

    /// <summary>A plugin's verbs.</summary>
    public IEnumerable<string> PluginVerbs(string plugin) => _plugins.TryGetValue(plugin, out var entry) ? entry.Verbs.Keys : [];

    /// <summary>Finds a plugin's inflections by their own name, across its verbs.</summary>
    public IReadOnlyList<ModuleRegistration> FindPluginInflection(string plugin, string name) =>
        _plugins.TryGetValue(plugin, out var entry) ? [.. entry.Verbs.Values.SelectMany(inflections => inflections).Where(r => r.Inflection == name)] : [];

    private void Share(string name, string first, string second)
    {
        if (!_shared.TryGetValue(name, out var plugins))
            _shared[name] = plugins = [first];
        if (!plugins.Contains(second))
            plugins.Add(second);
    }

    private string? PluginOf(Type messageType) =>
        _plugins.Values.FirstOrDefault(p => p.MessageTypes.ContainsValue(messageType))?.Name;

    /// <summary>The verbs registered so far.</summary>
    public IEnumerable<string> Verbs => _verbs.Keys;

    /// <summary>The inflections' own names, across every verb.</summary>
    public IEnumerable<string> Inflections => _verbs.Values.SelectMany(inflections => inflections).Select(r => r.Inflection).Distinct();

    /// <summary>The library's books by name, plugins' included, both under their own names and as <c>Plugin.Book</c>.</summary>
    public IEnumerable<string> Books => _books.Where(b => !_sharedBooks.ContainsKey(b.Key)).Select(b => b.Value.Name);

    /// <summary>The plugins whose books share this name, if two or more do, so only the plugin's name tells them apart.</summary>
    public IReadOnlyList<string>? BookSharedBy(string name) => _sharedBooks.TryGetValue(name, out var plugins) ? plugins : null;

    /// <summary>
    /// Finds a book by name, ignoring case, and reads its items: its own name, or <c>Plugin.Book</c> for a plugin's. The
    /// book found has the name as the library spells it. A name two plugins' books share isn't found.
    /// </summary>
    public bool TryGetBook(string name, [NotNullWhen(true)] out Book? book)
    {
        book = null;
        if (_sharedBooks.ContainsKey(name) || !_books.TryGetValue(name, out var entry))
            return false;
        book = new Book(entry.Name, entry.Read() ?? []);
        return true;
    }

    /// <summary>Adds a book to the library by hand, replacing one of the same name.</summary>
    public ModuleRegistry AddBook(string name, IReadOnlyList<string> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(items);
        _books[name] = new LibraryEntry(name, () => items);
        return this;
    }

    // The public static members of an assembly's public types marked [LibraryBook], each read only when it's needed.
    private static Dictionary<string, LibraryEntry> FindBooks(Assembly assembly)
    {
        var books = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Static))
            {
                if (member.GetCustomAttribute<LibraryBookAttribute>() is not { } attribute)
                    continue;
                Func<IReadOnlyList<string>?> read = member switch
                {
                    PropertyInfo property when typeof(IReadOnlyList<string>).IsAssignableFrom(property.PropertyType) && property.GetMethod is not null
                        => () => (IReadOnlyList<string>?)property.GetValue(null),
                    FieldInfo field when typeof(IReadOnlyList<string>).IsAssignableFrom(field.FieldType)
                        => () => (IReadOnlyList<string>?)field.GetValue(null),
                    _ => throw new ArgumentException(
                        $"{type.Name}.{member.Name} is marked as the book {attribute.Name}, but a book has to be a public static property or field that's a list of strings."),
                };
                if (string.IsNullOrWhiteSpace(attribute.Name))
                    throw new ArgumentException($"{type.Name}.{member.Name} is marked as a book without a name.");
                if (!books.TryAdd(attribute.Name, new LibraryEntry(attribute.Name, read)))
                    throw new ArgumentException($"There are two books called {attribute.Name}. Give one of them another name.");
            }
        }
        return books;
    }

    /// <summary>The message type names registered so far.</summary>
    public IEnumerable<string> MessageTypeNames => _messageTypes.Keys;

    /// <summary>Registers every public module class with a <see cref="VerbAttribute"/> in an assembly, and its public message types.</summary>
    public ModuleRegistry AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var (name, book) in FindBooks(assembly))
            _books[name] = book;
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract)
                continue;
            if (type.GetCustomAttribute<VerbAttribute>() is not null && typeof(AModule).IsAssignableFrom(type))
                Add(type);
            else if (typeof(Message).IsAssignableFrom(type))
                AddMessageType(type);
        }
        return this;
    }

    /// <summary>
    /// Registers one module class with a <see cref="VerbAttribute"/>, as an inflection of its verb. It needs a public
    /// constructor that takes its parameters record, or a public parameterless one if it has no parameters.
    /// </summary>
    public ModuleRegistry Add(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        foreach (var registration in Registrations(moduleType, plugin: null))
            Register(registration);
        return this;
    }

    // An inflection's registration under its verb's name, and another under its alias if it has one (Q264), so a
    // pipeline may write either, and tools and errors show the one written.
    private static IEnumerable<ModuleRegistration> Registrations(Type moduleType, string? plugin)
    {
        var registration = Registration(moduleType, plugin);
        yield return registration;
        if (moduleType.GetCustomAttribute<VerbAttribute>()?.Alias is { Length: > 0 } alias && alias != registration.Verb)
            yield return Registration(moduleType, plugin, alias);
    }

    private static ModuleRegistration Registration(Type moduleType, string? plugin, string? alias = null)
    {
        var attribute = moduleType.GetCustomAttribute<VerbAttribute>()
            ?? throw new ArgumentException($"{moduleType.Name} has no [Verb] attribute.", nameof(moduleType));
        var inflection = attribute.Inflection ?? ExpressionBinder.FriendlyName(moduleType);
        var verb = alias ?? attribute.Name;
        var aliasOf = alias is null ? null : attribute.Name;

        var constructors = moduleType.GetConstructors();
        var withParameters = constructors.FirstOrDefault(c => c.GetParameters() is [{ ParameterType.IsClass: true }]);
        if (withParameters is not null)
        {
            var parametersType = withParameters.GetParameters()[0].ParameterType;
            return new ModuleRegistration(verb, inflection, moduleType, parametersType, p => (AModule)withParameters.Invoke([p])) { Plugin = plugin, AliasOf = aliasOf };
        }
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? throw new ArgumentException($"{moduleType.Name} needs a public constructor that takes its parameters, or none.", nameof(moduleType));
        return new ModuleRegistration(verb, inflection, moduleType, null, _ => (AModule)parameterless.Invoke(null)) { Plugin = plugin, AliasOf = aliasOf };
    }

    /// <summary>
    /// Registers a module without parameters, made by <paramref name="create"/>. Registering the same class again
    /// replaces it.
    /// </summary>
    /// <param name="inflection">The inflection's name. Defaults to the one in the class's <see cref="VerbAttribute"/>, or the class name.</param>
    public ModuleRegistry Add<TModule>(string verb, Func<TModule> create, string? inflection = null)
        where TModule : AModule
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, inflection ?? InflectionOf(typeof(TModule)), typeof(TModule), null, _ => create()));
    }

    /// <summary>
    /// Registers a module with a parameters record, made by <paramref name="create"/>. Registering the same class
    /// again replaces it, as the meep command does to give WriteLine its output.
    /// </summary>
    /// <param name="inflection">The inflection's name. Defaults to the one in the class's <see cref="VerbAttribute"/>, or the class name.</param>
    public ModuleRegistry Add<TModule, TParameters>(string verb, Func<TParameters, TModule> create, string? inflection = null)
        where TModule : AModule
        where TParameters : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, inflection ?? InflectionOf(typeof(TModule)), typeof(TModule), typeof(TParameters), p => create((TParameters)p!)));
    }

    /// <summary>Lets <c>is</c> name a message type.</summary>
    public ModuleRegistry AddMessageType(Type type, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Message).IsAssignableFrom(type) && !type.IsInterface)
            throw new ArgumentException($"{type.Name} isn't a message type.", nameof(type));
        _messageTypes[name ?? ExpressionBinder.FriendlyName(type)] = type;
        return this;
    }

    /// <summary>Finds a verb's inflections, in the order they were registered.</summary>
    public bool TryGetVerb(string verb, [NotNullWhen(true)] out IReadOnlyList<ModuleRegistration>? inflections)
    {
        var found = _verbs.TryGetValue(verb, out var list);
        inflections = list;
        return found;
    }

    /// <summary>Finds inflections by their own name, across every verb.</summary>
    public IReadOnlyList<ModuleRegistration> FindInflection(string name) =>
        [.. _verbs.Values.SelectMany(inflections => inflections).Where(r => r.Inflection == name)];

    /// <summary>Finds a message type by name: its own, or <c>Plugin.Name</c> for a plugin's. A name two plugins share isn't found.</summary>
    public bool TryGetMessageType(string name, [NotNullWhen(true)] out Type? type)
    {
        type = null;
        return !_shared.ContainsKey(name) && _messageTypes.TryGetValue(name, out type);
    }

    private static string InflectionOf(Type moduleType) =>
        moduleType.GetCustomAttribute<VerbAttribute>()?.Inflection ?? ExpressionBinder.FriendlyName(moduleType);

    private ModuleRegistry Register(ModuleRegistration registration)
    {
        if (!_verbs.TryGetValue(registration.Verb, out var inflections))
            _verbs[registration.Verb] = inflections = [];

        var index = inflections.FindIndex(r => r.Inflection == registration.Inflection);
        if (index >= 0 && inflections[index].ModuleType == registration.ModuleType)
            inflections[index] = registration;
        else if (Problem(inflections, registration) is { } problem)
            throw new ArgumentException(problem);
        else
            inflections.Add(registration);

        foreach (var emitted in registration.Emits)
            AddMessageType(emitted);
        return this;
    }

    // Why an inflection can't join a verb's others, or null if it can.
    private static string? Problem(List<ModuleRegistration> inflections, ModuleRegistration registration)
    {
        if (inflections.FirstOrDefault(r => r.Inflection == registration.Inflection) is { } same)
            return $"{registration.Verb}.{registration.Inflection} is already provided by {same.ModuleType.Name}. Give one of them another inflection name.";
        if (inflections.Count == 0)
            return null;
        var first = inflections[0];
        if (first.IsSource != registration.IsSource)
            return $"{registration.Verb}'s inflections have to be all sources or none, but {first.Inflection} and {registration.Inflection} differ.";
        if (first.IsPassthrough != registration.IsPassthrough)
            return $"{registration.Verb}'s inflections have to be all passthrough modules or none, but {first.Inflection} and {registration.Inflection} differ.";
        if (first.Role != registration.Role)
            return $"{registration.Verb}'s inflections have to agree on their role, but {first.Inflection} is {Article(first.Role)} and {registration.Inflection} is {Article(registration.Role)}. Give them the same [Role].";
        // An inflection chosen for each message is chosen after the gate, which can only tell what a message needs by
        // its type, so the verb's inflections must need the same permissions (Q293).
        foreach (var other in inflections.Where(r => r.ChosenForEachMessage || registration.ChosenForEachMessage))
        {
            if (!new HashSet<SDK.Security.PermissionName>(other.RequiredPermissions).SetEquals(registration.RequiredPermissions))
            {
                return $"{registration.Verb}.{other.Inflection} and {registration.Verb}.{registration.Inflection} require different permissions ([RequiresPermission]), " +
                    "but they're chosen for each message ([ChosenForEachMessage]), after the gate. Give them the same permissions, or have them take different message types.";
            }
        }
        return null;
    }

    private static string Article(VerbRole role) => role is VerbRole.Inlet or VerbRole.Outlet ? $"an {role}" : $"a {role}";

    // A plugin's verbs and message types, under its name.
    private sealed class PluginEntry(string name)
    {
        public string Name { get; } = name;

        public Dictionary<string, List<ModuleRegistration>> Verbs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Type> MessageTypes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Books { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // A book: its name as the library spells it, and how to read its items.
    private sealed record LibraryEntry(string Name, Func<IReadOnlyList<string>?> Read);
}
