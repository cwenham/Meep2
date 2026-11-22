using System.Linq.Expressions;
using System.Reflection;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;

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

    /// <summary>
    /// Compiles a template for messages of one type into its pieces, each formatted as text, in order, without joining
    /// them, so a module can tell the text written in the pipeline from what the message gave.
    /// </summary>
    /// <exception cref="BindException">The template doesn't fit messages of this type, or is wrong in itself.</exception>
    public static Func<Message, string[]> CompilePieces(TemplateSyntax template, CompileScope scope, Type messageType, bool bigNumbers = false)
    {
        var parameter = Expression.Parameter(typeof(Message), "message");
        var binder = new ExpressionBinder(scope, messageType, parameter, bigNumbers);
        return Expression.Lambda<Func<Message, string[]>>(binder.TemplateParts(template), parameter).Compile();
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
