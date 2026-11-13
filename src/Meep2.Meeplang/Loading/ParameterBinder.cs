using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>A module's parameters, filled in from its Meeplang line.</summary>
internal sealed class BoundParameters
{
    /// <summary>The parameters record, or null if the module takes none.</summary>
    public object? Value { get; set; }

    /// <summary>Parameters worked out for each message, for checking and for deciding which types skip the module.</summary>
    public List<ICheckedComputed> Computed { get; } = [];

    /// <summary>Modules named by <see cref="ModuleReference"/> parameters, which feed this one.</summary>
    public List<(string Name, SourceSpan Span)> References { get; } = [];

    public bool Failed { get; set; }
}

/// <summary>
/// Fills in a module's parameters record from its value, its <c>name=value</c> parameters and the parameter lines
/// under it. Parameter names match the record's properties ignoring case and hyphens, so <c>max-size</c> sets
/// <c>MaxSize</c>.
/// </summary>
internal sealed class ParameterBinder(CompileScope scope, Diagnostics diagnostics, PipelineSyntax pipeline, ModuleRegistry registry)
{
    public BoundParameters Bind(ModuleSyntax module, ModuleRegistration registration)
    {
        var result = new BoundParameters();
        var type = registration.ParametersType;
        if (type is null)
        {
            if (module.Positional is not null)
                Error(result, $"{module.Verb} doesn't take a value.", module.Positional.Span);
            foreach (var argument in module.Arguments)
                Error(result, $"{module.Verb} doesn't take any parameters, so {argument.Name} isn't allowed.", argument.NameSpan);
            return result;
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(type)!;
        }
        catch (MissingMethodException)
        {
            Error(result, $"{module.Verb}'s parameters ({type.Name}) need a public constructor without arguments.", module.VerbSpan);
            return result;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .ToList();
        var positional = properties.FirstOrDefault(p => p.IsDefined(typeof(PositionalAttribute)));
        var assigned = new HashSet<PropertyInfo>();

        if (module.Positional is { } value)
        {
            if (positional is null)
            {
                Error(result, $"{module.Verb} doesn't take a value straight after its name. Name its parameters instead, as in name=value.", value.Span);
            }
            else
            {
                assigned.Add(positional);
                Assign(instance, positional, value, result);
            }
        }

        foreach (var argument in module.Arguments)
        {
            var property = properties.FirstOrDefault(p => Normalize(p.Name) == Normalize(argument.Name));
            if (property is null)
            {
                Error(result, $"{module.Verb} has no parameter called {argument.Name}." + Suggestions.For(argument.Name, properties.Select(p => Kebab(p.Name))), argument.NameSpan);
                continue;
            }
            if (!assigned.Add(property))
            {
                Error(result, $"{Kebab(property.Name)} is given more than once.", argument.NameSpan);
                continue;
            }
            Assign(instance, property, argument.Value, result);
        }

        foreach (var property in properties.Where(p => p.IsDefined(typeof(RequiredMemberAttribute)) && !assigned.Contains(p)))
        {
            Error(
                result,
                property == positional
                    ? $"{module.Verb} needs a value after its name, for its {Kebab(property.Name)}."
                    : $"{module.Verb} needs {Kebab(property.Name)}=… on its line, or a {Kebab(property.Name)} = … line under it.",
                module.VerbSpan);
        }

        result.Value = instance;
        return result;
    }

    /// <summary>A property name as it's written in Meeplang: <c>MaxSize</c> becomes <c>max-size</c>.</summary>
    public static string Kebab(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    private static string Normalize(string name) =>
        name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    private void Assign(object instance, PropertyInfo property, ExpressionSyntax syntax, BoundParameters result)
    {
        try
        {
            var value = Convert(syntax, property.PropertyType, Kebab(property.Name), result);
            if (value is ICheckedComputed computed)
            {
                // Parameters for errors, and the rest when there are some, are only worked out for their own kind.
                computed.ForErrors = property.IsDefined(typeof(ForErrorsAttribute))
                    ? true
                    : instance.GetType().GetProperties().Any(p => p.IsDefined(typeof(ForErrorsAttribute))) ? false : null;
            }
            property.SetValue(instance, value);
        }
        catch (BindException exception)
        {
            Error(result, exception.Message, exception.Span);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException or FormatException or InvalidOperationException)
        {
            Error(result, $"{Kebab(property.Name)}: {Clean(exception.InnerException.Message)}", syntax.Span);
        }
    }

    private object? Convert(ExpressionSyntax syntax, Type target, string name, BoundParameters result)
    {
        var type = Nullable.GetUnderlyingType(target) ?? target;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Computed<>))
        {
            var valueType = type.GetGenericArguments()[0];
            if (!ExpressionCompiler.UsesMessage(syntax))
            {
                // The same for every message: work it out once.
                var value = ConvertConstant(ExpressionCompiler.Evaluate(syntax, scope), valueType, syntax.Span);
                return type.GetMethod(nameof(Computed<object>.Constant))!.Invoke(null, [value]);
            }
            var computed = Activator.CreateInstance(
                typeof(CompiledComputed<>).MakeGenericType(valueType),
                syntax,
                scope,
                pipeline.TextOf(syntax.Span))!;
            result.Computed.Add((ICheckedComputed)computed);
            return computed;
        }

        if (type == typeof(ModuleReference))
        {
            if (syntax is not NameSyntax module)
                throw new BindException($"Expected the name of a module for {name}.", syntax.Span);
            result.References.Add((module.Name, module.Span));
            return new ModuleReference(module.Name);
        }

        if (type == typeof(Book))
            return FindBook(syntax, name);

        if (type.IsEnum)
        {
            if (syntax is NameSyntax choice && Enum.TryParse(type, choice.Name, ignoreCase: true, out var parsed))
                return parsed;
            throw new BindException($"{name} needs one of: {string.Join(", ", Enum.GetNames(type).Select(n => n.ToLowerInvariant()))}.", syntax.Span);
        }

        if (ExpressionCompiler.UsesMessage(syntax))
            throw new BindException($"{name} has to be the same for every message, so it can't use msg or fam.", syntax.Span);
        return ConvertConstant(ExpressionCompiler.Evaluate(syntax, scope), type, syntax.Span);
    }

    // A book from the library, named by text that's the same for every message.
    private Book FindBook(ExpressionSyntax syntax, string name)
    {
        if (ExpressionCompiler.UsesMessage(syntax) || ExpressionCompiler.Evaluate(syntax, scope).Value is not string title)
            throw new BindException($"{name} needs the name of a book from the library, such as \"Countries\".", syntax.Span);
        if (registry.BookSharedBy(title) is { } plugins)
        {
            throw new BindException(
                $"The plugins {string.Join(" and ", plugins)} both have a book called {title}, so name it with its plugin's name, as in \"{plugins[0]}.{title}\".",
                syntax.Span);
        }
        if (!registry.TryGetBook(title, out var book))
            throw new BindException($"There's no book called {title} in the library." + Suggestions.For(title, registry.Books), syntax.Span);
        if (book.Items.Count == 0)
            throw new BindException($"The book {book.Name} is empty.", syntax.Span);
        return book;
    }

    private static object? ConvertConstant(ConstantValue constant, Type target, SourceSpan span)
    {
        var value = constant.Value;
        if (value is null)
        {
            return !target.IsValueType || Nullable.GetUnderlyingType(target) is not null
                ? null
                : throw new BindException($"Expected {Describe(target)}, not null.", span);
        }
        if (target.IsInstanceOfType(value))
            return value;
        if (Dynamic.TryConvert(value, target, out var converted))
            return converted;

        // Types of their own that read themselves from text, such as JsonPath.
        if (value is string text && !IsStandard(target) && FindParse(target) is { } parse)
        {
            try
            {
                return parse.Invoke(null, [text, CultureInfo.InvariantCulture]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is FormatException or ArgumentException)
            {
                throw new BindException(exception.InnerException.Message, span);
            }
        }
        if (Numbers.IsNumber(value) && Numbers.IsNumericType(target))
            throw new BindException(Numbers.WhyNot(value, target), span);
        throw new BindException($"Expected {Describe(target)}, but this is {Dynamic.Describe(value)}.", span);
    }

    private static bool IsStandard(Type type) =>
        type.IsPrimitive || type == typeof(decimal) || type == typeof(string) || type == typeof(TimeSpan) || type == typeof(DateTime);

    private static MethodInfo? FindParse(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>))
            ? type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IFormatProvider)])
            : null;

    private static string Describe(Type type)
    {
        if (type == typeof(string))
            return "text";
        if (type == typeof(TimeSpan))
            return "a duration, such as 5s or 250ms";
        if (type == typeof(bool))
            return "true or false";
        if (Numbers.IsNumericType(type))
            return Numbers.IsWholeNumberType(type) ? "a whole number" : "a number";
        return $"a {ExpressionBinder.FriendlyName(type)}";
    }

    // ArgumentException adds " (Parameter 'name')" to its message, which means nothing to someone writing Meeplang.
    private static string Clean(string message)
    {
        var index = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return index < 0 ? message : message[..index];
    }

    private void Error(BoundParameters result, string message, SourceSpan span)
    {
        diagnostics.Error(message, span);
        result.Failed = true;
    }
}
