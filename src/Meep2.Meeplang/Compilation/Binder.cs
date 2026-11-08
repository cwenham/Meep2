using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;
using Meep2.SDK.Numbers;

namespace Meep2.Meeplang.Compilation;

/// <summary>
/// Compiles expressions to .NET code, for one message type at a time (gradual typing). Where a value's type is
/// known, members, operators and methods are checked and compiled directly. Where it isn't, such as a member of a
/// JSON message, the compiled code asks <see cref="Dynamic"/> when each message arrives.
/// </summary>
internal static class ExpressionCompiler
{
    /// <summary>
    /// Compiles an expression for messages of one type. Sums are done in decimal unless <paramref name="bigNumbers"/>
    /// is set; then they're done by <see cref="Numbers.Arithmetic"/>, which carries on with big numbers when an
    /// answer doesn't fit in a decimal.
    /// </summary>
    /// <exception cref="BindException">The expression doesn't fit messages of this type, or is wrong in itself.</exception>
    public static Func<Message, T> Compile<T>(ExpressionSyntax syntax, CompileScope scope, Type messageType, bool bigNumbers = false)
    {
        var parameter = Expression.Parameter(typeof(Message), "message");
        var binder = new ExpressionBinder(scope, messageType, parameter, bigNumbers);
        var body = binder.ConvertTo(binder.Bind(syntax), typeof(T), syntax.Span);
        return Expression.Lambda<Func<Message, T>>(body, parameter).Compile();
    }

    /// <summary>Works out an expression that doesn't depend on a message, such as a constant's value.</summary>
    /// <exception cref="BindException">The expression is wrong, uses msg or fam, or fails when worked out.</exception>
    public static ConstantValue Evaluate(ExpressionSyntax syntax, CompileScope scope)
    {
        try
        {
            return Evaluate(syntax, scope, bigNumbers: false);
        }
        catch (BeyondDecimalException)
        {
            return Evaluate(syntax, scope, bigNumbers: true);
        }
    }

    private static ConstantValue Evaluate(ExpressionSyntax syntax, CompileScope scope, bool bigNumbers)
    {
        var binder = new ExpressionBinder(scope, messageType: null, Expression.Parameter(typeof(Message), "message"), bigNumbers);
        var body = binder.Bind(syntax);
        var function = Expression.Lambda<Func<object?>>(Expression.Convert(body, typeof(object))).Compile(preferInterpretation: true);
        try
        {
            var value = function();
            // A sum done with big numbers isn't typed until it's worked out: a decimal if it fits, otherwise a BigDecimal.
            return new ConstantValue(value, body.Type == typeof(object) && value is not null ? value.GetType() : body.Type);
        }
        catch (BeyondDecimalException)
        {
            throw;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is BeyondDecimalException)
        {
            throw new BeyondDecimalException();
        }
        catch (Exception exception) when (exception is not BindException)
        {
            throw new BindException((exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message, syntax.Span);
        }
    }

    /// <summary>Whether an expression reads <c>msg</c> or <c>fam</c>, so it has to be worked out for each message.</summary>
    public static bool UsesMessage(ExpressionSyntax syntax) => syntax switch
    {
        NameSyntax name => name.Name is "msg" or "fam",
        MemberSyntax member => UsesMessage(member.Target),
        CallSyntax call => UsesMessage(call.Callee) || call.Arguments.Any(UsesMessage),
        IndexSyntax index => UsesMessage(index.Target) || UsesMessage(index.Index),
        UnarySyntax unary => UsesMessage(unary.Operand),
        BinarySyntax binary => UsesMessage(binary.Left) || UsesMessage(binary.Right),
        IsSyntax test => UsesMessage(test.Operand),
        TemplateSyntax template => template.Parts.OfType<HolePart>().Any(hole => UsesMessage(hole.Expression)),
        _ => false,
    };
}

internal sealed class ExpressionBinder(CompileScope scope, Type? messageType, ParameterExpression message, bool bigNumbers = false)
{
    private static readonly MethodInfo s_objectEquals = typeof(object).GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Static, [typeof(object), typeof(object)])!;
    private static readonly MethodInfo s_compareOrdinal = typeof(string).GetMethod(nameof(string.CompareOrdinal), [typeof(string), typeof(string)])!;

    public Expression Bind(ExpressionSyntax syntax) => syntax switch
    {
        LiteralSyntax literal => Expression.Constant(literal.Value, literal.Value?.GetType() ?? typeof(object)),
        TemplateSyntax template => Template(template),
        NameSyntax name => Name(name),
        MemberSyntax member => Member(member),
        CallSyntax call => Call(call),
        IndexSyntax index => Index(index),
        UnarySyntax unary => Unary(unary),
        BinarySyntax binary => Binary(binary),
        IsSyntax test => Is(test),
        _ => throw new BindException($"Meeplang can't compile {syntax.GetType().Name} yet.", syntax.Span),
    };

    /// <summary>Converts a compiled value to the type a parameter needs.</summary>
    public Expression ConvertTo(Expression value, Type type, SourceSpan span)
    {
        if (value.Type == type)
            return value;
        if (type == typeof(object))
            return Box(value);
        if (type == typeof(string))
            return Format(value, format: null);
        if (type == typeof(bool))
            return ToBool(value, span);
        if (value.Type == typeof(object))
            return DynamicConvert(value, type);
        if (value is ConstantExpression { Value: { } constant } && Numbers.IsNumber(constant) && Numbers.IsNumericType(type))
        {
            // A number written in the file is checked now, so a mistake is reported when the pipeline loads.
            return Dynamic.TryConvert(constant, type, out var fitted)
                ? Expression.Constant(fitted, type)
                : throw new BindException(Numbers.WhyNot(constant, type), span);
        }
        if (TryConvertImplicitly(value, type, out var converted))
            return converted;
        throw new BindException($"Expected {Describe(type)}, but this is {Describe(value.Type)}.", span);
    }

    /// <summary>A type's name as people would say it in an error message.</summary>
    public static string FriendlyName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    internal static string Describe(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Numbers.IsWholeNumberType(type))
            return "a whole number";
        if (Numbers.IsNumericType(type))
            return "a number";
        if (type == typeof(string))
            return "text";
        if (type == typeof(bool))
            return "true or false";
        if (type == typeof(TimeSpan))
            return "a duration";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return "a date and time";
        return $"a {FriendlyName(type)}";
    }

    private Expression Template(TemplateSyntax template)
    {
        var parts = template.Parts.Select(part => part switch
        {
            TextPart text => (Expression)Expression.Constant(text.Text),
            HolePart hole => Format(Bind(hole.Expression), hole.Format),
            _ => throw new BindException("Unknown part of a template.", template.Span),
        });
        return Expression.Call(Dynamic.ConcatMethod, Expression.NewArrayInit(typeof(string), parts));
    }

    private Expression Name(NameSyntax name)
    {
        switch (name.Name)
        {
            case "msg":
                if (messageType is null)
                    throw new BindException("msg is only there when a message arrives, so it can't be used here.", name.Span);
                return messageType == typeof(Message) ? message : Expression.Convert(message, messageType);
            case "fam":
                throw new BindException("fam needs a module name after it, as in fam.Schedule.", name.Span);
        }
        if (scope.Constants.TryGetValue(name.Name, out var constant))
            return Expression.Constant(constant.Value, constant.Type);
        throw new BindException(
            $"There's no constant called {name.Name}." + Suggestions.For(name.Name, scope.Constants.Keys.Append("msg").Append("fam")),
            name.Span);
    }

    private Expression Member(MemberSyntax member)
    {
        if (member.Target is NameSyntax { Name: "fam" } fam)
        {
            if (messageType is null)
                throw new BindException("fam is only there when a message arrives, so it can't be used here.", fam.Span);
            var target = scope.Fam(member.Member)
                ?? throw new BindException($"There's no module called {member.Member} for fam to find." + Suggestions.For(member.Member, scope.ModuleNames), member.MemberSpan);
            var found = Expression.Call(Dynamic.FamMethod, message, Expression.Constant(member.Member));
            return target.Type is { } type ? Expression.Convert(found, type) : Expression.Convert(found, typeof(object));
        }
        return MemberOf(Bind(member.Target), member.Member, member.MemberSpan);
    }

    private static Expression MemberOf(Expression target, string name, SourceSpan span)
    {
        var type = target.Type;
        if (type == typeof(object))
            return Expression.Call(Dynamic.GetMemberMethod, target, Expression.Constant(name));
        if (FindProperty(type, name) is { } property)
            return Expression.Property(target, property);
        if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } field)
            return Expression.Field(target, field);
        if (typeof(IHasDynamicMembers).IsAssignableFrom(type) || type == typeof(JsonElement))
            return Expression.Call(Dynamic.GetMemberMethod, Box(target), Expression.Constant(name));
        throw new BindException($"{FriendlyName(type)} has no '{name}'." + Suggestions.For(name, MemberNames(type)), span);
    }

    private Expression Call(CallSyntax call)
    {
        if (call.Callee is MemberSyntax method && method.Target is not NameSyntax { Name: "fam" })
        {
            var target = Bind(method.Target);
            var arguments = call.Arguments.Select(Bind).ToArray();
            if (target.Type == typeof(object) || target.Type == typeof(JsonElement))
            {
                return Expression.Call(
                    Dynamic.InvokeMethod,
                    Box(target),
                    Expression.Constant(method.Member),
                    Expression.NewArrayInit(typeof(object), arguments.Select(Box)));
            }
            return TypedCall(target, method.Member, arguments, method.MemberSpan);
        }
        if (call.Callee is NameSyntax { Name: "secret" } secret)
            throw new BindException("secret() isn't supported yet. It arrives with the secret store.", secret.Span);
        if (call.Callee is NameSyntax function)
            throw new BindException($"There's no function called {function.Name}.", function.Span);
        throw new BindException("Only methods can be called, as in msg.Error.Contains(\"timeout\").", call.Span);
    }

    private static Expression TypedCall(Expression target, string name, Expression[] arguments, SourceSpan span)
    {
        var candidates = AllMethods(target.Type)
            .Where(m => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == arguments.Length)
            .ToList();

        // Prefer a method whose parameters match exactly, then one the values can be converted to.
        foreach (var exactOnly in new[] { true, false })
        {
            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                var converted = new Expression[arguments.Length];
                var fits = true;
                for (var i = 0; i < arguments.Length && fits; i++)
                {
                    if (exactOnly)
                    {
                        fits = arguments[i].Type == parameters[i].ParameterType;
                        converted[i] = arguments[i];
                    }
                    else
                    {
                        fits = TryConvertImplicitly(arguments[i], parameters[i].ParameterType, out converted[i]);
                    }
                }
                if (fits)
                    return Expression.Call(target, candidate, converted);
            }
        }

        var names = AllMethods(target.Type).Select(m => m.Name);
        throw new BindException(
            candidates.Count == 0
                ? $"{FriendlyName(target.Type)} has no method {name} that takes {arguments.Length} value(s)." + Suggestions.For(name, names)
                : $"{FriendlyName(target.Type)}.{name} can't take these values.",
            span);
    }

    private Expression Index(IndexSyntax index)
    {
        var target = Bind(index.Target);
        var position = Bind(index.Index);
        var type = target.Type;
        if (type.IsArray)
            return Expression.ArrayIndex(target, ConvertTo(position, typeof(int), index.Index.Span));
        if (type != typeof(object) && type != typeof(JsonElement) && !typeof(IHasDynamicMembers).IsAssignableFrom(type))
        {
            foreach (var indexer in AllProperties(type).Where(p => p.GetIndexParameters().Length == 1))
            {
                if (TryConvertImplicitly(position, indexer.GetIndexParameters()[0].ParameterType, out var converted))
                    return Expression.MakeIndex(target, indexer, [converted]);
            }
        }
        return Expression.Call(Dynamic.IndexMethod, Box(target), Box(position));
    }

    private Expression Unary(UnarySyntax unary)
    {
        var operand = Bind(unary.Operand);
        if (unary.Operator == "not")
            return Expression.Not(ToBool(operand, unary.Operand.Span));
        if (operand.Type == typeof(object))
            return Expression.Call(Dynamic.NegateMethod, operand);
        switch (Numbers.KindOf(operand.Type))
        {
            case NumberKind.Exact:
                return Expression.Negate(ToDecimal(operand));
            case NumberKind.Approximate:
                return Expression.Negate(ToDouble(operand));
            case NumberKind.Big:
                return Expression.Call(Numbers.NegateMethod, Box(operand));
        }
        try
        {
            return Expression.Negate(operand);
        }
        catch (InvalidOperationException)
        {
            throw new BindException($"Can't make {Describe(operand.Type)} negative.", unary.Span);
        }
    }

    private Expression Binary(BinarySyntax binary)
    {
        var left = Bind(binary.Left);
        var right = Bind(binary.Right);
        return binary.Operator switch
        {
            "and" => Expression.AndAlso(ToBool(left, binary.Left.Span), ToBool(right, binary.Right.Span)),
            "or" => Expression.OrElse(ToBool(left, binary.Left.Span), ToBool(right, binary.Right.Span)),
            "+" or "-" or "*" or "/" or "%" => Arithmetic(binary.Operator, left, right, binary.Span),
            _ => Comparison(binary.Operator, left, right, binary.Span),
        };
    }

    private Expression Arithmetic(string op, Expression left, Expression right, SourceSpan span)
    {
        if (left.Type == typeof(object) || right.Type == typeof(object))
            return Expression.Call(Dynamic.ArithmeticMethod, Expression.Constant(op), Box(left), Box(right));
        if (op == "+" && (left.Type == typeof(string) || right.Type == typeof(string)))
            return Expression.Call(Dynamic.ConcatMethod, Expression.NewArrayInit(typeof(string), Format(left, null), Format(right, null)));

        var leftKind = Numbers.KindOf(left.Type);
        var rightKind = Numbers.KindOf(right.Type);
        if (leftKind != NumberKind.None && rightKind != NumberKind.None)
        {
            // A double is approximate already, so a sum with one is done in double.
            if (leftKind == NumberKind.Approximate || rightKind == NumberKind.Approximate)
            {
                var a = ToDouble(left);
                var b = ToDouble(right);
                return op switch
                {
                    "+" => Expression.Add(a, b),
                    "-" => Expression.Subtract(a, b),
                    "*" => Expression.Multiply(a, b),
                    "/" => Expression.Call(Numbers.DivideApproximateMethod, a, b),
                    _ => Expression.Call(Numbers.RemainderApproximateMethod, a, b),
                };
            }

            // Big numbers, or a sum that has already gone beyond decimal for this message type.
            if (bigNumbers || leftKind == NumberKind.Big || rightKind == NumberKind.Big)
                return Expression.Call(Numbers.ArithmeticMethod, Expression.Constant(op), Box(left), Box(right));

            var method = op switch
            {
                "+" => Numbers.AddMethod,
                "-" => Numbers.SubtractMethod,
                "*" => Numbers.MultiplyMethod,
                "/" => Numbers.DivideMethod,
                _ => Numbers.RemainderMethod,
            };
            return Expression.Call(method, ToDecimal(left), ToDecimal(right));
        }

        var kind = op switch
        {
            "+" => ExpressionType.Add,
            "-" => ExpressionType.Subtract,
            "*" => ExpressionType.Multiply,
            "/" => ExpressionType.Divide,
            _ => ExpressionType.Modulo,
        };
        try
        {
            return Expression.MakeBinary(kind, left, right);
        }
        catch (InvalidOperationException)
        {
            throw new BindException($"Can't use {op} with {Describe(left.Type)} and {Describe(right.Type)}.", span);
        }
    }

    private static Expression Comparison(string op, Expression left, Expression right, SourceSpan span)
    {
        if (left.Type == typeof(object) || right.Type == typeof(object))
            return Expression.Call(Dynamic.CompareMethod, Expression.Constant(op), Box(left), Box(right));

        var kind = op switch
        {
            "==" => ExpressionType.Equal,
            "!=" => ExpressionType.NotEqual,
            "<" => ExpressionType.LessThan,
            "<=" => ExpressionType.LessThanOrEqual,
            ">" => ExpressionType.GreaterThan,
            _ => ExpressionType.GreaterThanOrEqual,
        };
        var equality = op is "==" or "!=";

        var leftKind = Numbers.KindOf(left.Type);
        var rightKind = Numbers.KindOf(right.Type);
        if (leftKind != NumberKind.None && rightKind != NumberKind.None)
        {
            if (leftKind == NumberKind.Approximate || rightKind == NumberKind.Approximate)
                return Expression.MakeBinary(kind, ToDouble(left), ToDouble(right));
            if (leftKind == NumberKind.Big || rightKind == NumberKind.Big)
                return Expression.Call(Dynamic.CompareMethod, Expression.Constant(op), Box(left), Box(right));
            // Whole numbers compare exactly as themselves, which saves converting them to decimal.
            if (WholeType(left) is { } leftWhole && WholeType(right) is { } rightWhole)
            {
                var common = leftWhole == typeof(long) || rightWhole == typeof(long) ? typeof(long) : typeof(int);
                return Expression.MakeBinary(kind, AsWhole(left, common), AsWhole(right, common));
            }
            return Expression.MakeBinary(kind, ToDecimal(left), ToDecimal(right));
        }
        if (left.Type == typeof(string) && right.Type == typeof(string) && !equality)
        {
            var order = Expression.Call(s_compareOrdinal, left, right);
            return Expression.MakeBinary(kind, order, Expression.Constant(0));
        }
        if ((leftKind == NumberKind.None) != (rightKind == NumberKind.None) && (left.Type == typeof(string) || right.Type == typeof(string)))
            throw new BindException($"Can't compare {Describe(left.Type)} with {Describe(right.Type)}.", span);

        try
        {
            return Expression.MakeBinary(kind, left, right);
        }
        catch (InvalidOperationException)
        {
            if (equality)
            {
                if (!left.Type.IsAssignableFrom(right.Type) && !right.Type.IsAssignableFrom(left.Type))
                    throw new BindException($"Can't compare {Describe(left.Type)} with {Describe(right.Type)}.", span);
                var equal = Expression.Call(s_objectEquals, Box(left), Box(right));
                return op == "==" ? equal : Expression.Not(equal);
            }
            if (typeof(IComparable).IsAssignableFrom(left.Type) && left.Type == right.Type)
                return Expression.Call(Dynamic.CompareMethod, Expression.Constant(op), Box(left), Box(right));
            throw new BindException($"Can't compare {Describe(left.Type)} with {Describe(right.Type)} using {op}.", span);
        }
    }

    private Expression Is(IsSyntax test)
    {
        var type = scope.MessageType(test.TypeName)
            ?? throw new BindException($"There's no message type called {test.TypeName}.", test.Span);
        Expression result = Expression.TypeIs(Box(Bind(test.Operand)), type);
        return test.Negated ? Expression.Not(result) : result;
    }

    private static Expression ToBool(Expression value, SourceSpan span)
    {
        if (value.Type == typeof(bool))
            return value;
        if (value.Type == typeof(bool?))
            return Expression.Equal(value, Expression.Constant(true, typeof(bool?)));
        if (value.Type == typeof(object))
            return Expression.Call(Dynamic.IsTrueMethod, value);
        throw new BindException($"This is {Describe(value.Type)}, not true or false.", span);
    }

    private static Expression Format(Expression value, string? format) =>
        value.Type == typeof(string) && format is null
            ? value
            : Expression.Call(Dynamic.FormatMethod, Box(value), Expression.Constant(format, typeof(string)));

    // A constant is boxed once, when the expression is compiled, rather than each time it's worked out.
    private static Expression Box(Expression value) =>
        value.Type == typeof(object) ? value
        : value is ConstantExpression constant ? Expression.Constant(constant.Value, typeof(object))
        : Expression.Convert(value, typeof(object));

    private static Expression DynamicConvert(Expression value, Type type) =>
        Expression.Convert(Expression.Call(Dynamic.ConvertToMethod, Box(value), Expression.Constant(type, typeof(Type))), type);

    // A number as a decimal, for a typed sum. Only for decimal and the whole-number types up to 64 bits.
    private static Expression ToDecimal(Expression value)
    {
        if (value.Type == typeof(decimal))
            return value;
        var type = Nullable.GetUnderlyingType(value.Type);
        if (type is not null)
            value = Expression.Convert(value, type);
        return value.Type == typeof(decimal) ? value : Expression.Convert(value, typeof(decimal));
    }

    // A number of any kind as a double.
    private static Expression ToDouble(Expression value)
    {
        if (value.Type == typeof(double))
            return value;
        var type = Nullable.GetUnderlyingType(value.Type);
        if (type is not null)
            value = Expression.Convert(value, type);
        return value.Type == typeof(double) ? value : Expression.Convert(value, typeof(double));
    }

    // The whole-number type to compare a value as, if it's a whole number that fits in a long: int or long.
    private static Type? WholeType(Expression value)
    {
        if (value is ConstantExpression { Value: decimal constant })
        {
            if (!decimal.IsInteger(constant))
                return null;
            return constant is >= int.MinValue and <= int.MaxValue ? typeof(int)
                : constant is >= long.MinValue and <= long.MaxValue ? typeof(long)
                : null;
        }
        var type = value.Type;
        if (type == typeof(int) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ushort))
            return typeof(int);
        return type == typeof(long) || type == typeof(uint) ? typeof(long) : null;
    }

    private static Expression AsWhole(Expression value, Type type)
    {
        if (value is ConstantExpression { Value: decimal constant })
            return type == typeof(int) ? Expression.Constant((int)constant) : Expression.Constant((long)constant);
        return value.Type == type ? value : Expression.Convert(value, type);
    }

    private static bool TryConvertImplicitly(Expression value, Type type, out Expression converted)
    {
        converted = value;
        if (value.Type == type)
            return true;
        if (type.IsAssignableFrom(value.Type))
        {
            converted = Expression.Convert(value, type);
            return true;
        }
        if (value.Type == typeof(object))
        {
            converted = DynamicConvert(value, type);
            return true;
        }
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (IsWidening(value.Type, target) || (target == value.Type && target != type))
        {
            converted = Expression.Convert(value, type);
            return true;
        }
        return TryConvertNumber(value, type, out converted);
    }

    // Numbers convert to any numeric type: a constant now, if it fits; exact numbers to doubles and decimals directly;
    // and anything to a whole-number type or a decimal when each message arrives, if it's whole and fits.
    private static bool TryConvertNumber(Expression value, Type type, out Expression converted)
    {
        converted = value;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var from = Numbers.KindOf(value.Type);
        if (from == NumberKind.None || Numbers.KindOf(target) == NumberKind.None)
            return false;

        if (value is ConstantExpression { Value: { } constant })
        {
            if (!Dynamic.TryConvert(constant, type, out var fitted))
                return false;
            converted = Expression.Constant(fitted, type);
            return true;
        }

        Expression result;
        if (Numbers.KindOf(target) == NumberKind.Approximate)
            result = target == typeof(double) ? ToDouble(value) : Expression.Convert(ToDouble(value), target);
        else if (target == typeof(decimal) && from == NumberKind.Exact)
            result = ToDecimal(value);
        else if (target == typeof(BigDecimal) && from == NumberKind.Exact)
            result = Expression.Convert(ToDecimal(value), typeof(BigDecimal));
        else
            result = DynamicConvert(value, target);
        converted = result.Type == type ? result : Expression.Convert(result, type);
        return true;
    }

    private static bool IsWidening(Type from, Type to)
    {
        static int Rank(Type t) =>
            t == typeof(byte) || t == typeof(short) || t == typeof(int) ? 1
            : t == typeof(long) ? 2
            : t == typeof(float) || t == typeof(double) ? 3
            : 0;
        var fromRank = Rank(from);
        var toRank = Rank(to);
        return fromRank > 0 && toRank > fromRank;
    }

    // Walks base types first-declared-wins, so a property a record redeclares doesn't look ambiguous.
    private static PropertyInfo? FindProperty(Type type, string name)
    {
        foreach (var candidate in AllProperties(type))
        {
            if (candidate.Name == name && candidate.GetIndexParameters().Length == 0 && candidate.GetMethod is { IsPublic: true })
                return candidate;
        }
        return null;
    }

    private static IEnumerable<PropertyInfo> AllProperties(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                yield return property;
        }
        if (type.IsInterface)
        {
            foreach (var inherited in type.GetInterfaces())
            {
                foreach (var property in inherited.GetProperties())
                    yield return property;
            }
        }
    }

    private static IEnumerable<MethodInfo> AllMethods(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
        if (type.IsInterface)
            methods = methods.Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()));
        return methods.Where(m => !m.IsSpecialName);
    }

    private static IEnumerable<string> MemberNames(Type type) =>
        AllProperties(type).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name)
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name));
}
