using System.Runtime.CompilerServices;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Compilation;

/// <summary>
/// A Meeplang condition on messages of any type, such as <c>msg is Tick and msg.Count &gt; 2</c>, as a debug tap's
/// filter uses it. It's compiled for each type of message the first time it meets one. A type it doesn't fit, such
/// as one without the member it reads, doesn't match, and nor does a message it fails on.
/// </summary>
public sealed class MessageCondition
{
    private readonly ExpressionSyntax _syntax;
    private readonly CompileScope _scope;

    // Weak, so a plugin's message types don't keep the plugin loaded. A null function means the type doesn't fit.
    private readonly ConditionalWeakTable<Type, StrongBox<Func<Message, bool>?>> _compiled = new();

    private MessageCondition(string text, ExpressionSyntax syntax, CompileScope scope)
    {
        Text = text;
        _syntax = syntax;
        _scope = scope;
    }

    /// <summary>The condition as written.</summary>
    public string Text { get; }

    /// <summary>Reads a condition. <paramref name="registry"/> gives the message types <c>is</c> can name.</summary>
    /// <exception cref="FormatException">It isn't a condition Meeplang can read, or it names a message type that doesn't exist.</exception>
    public static MessageCondition Parse(string text, ModuleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var diagnostics = new Diagnostics(path: null);
        var tokens = Lexer.Lex(text, 1, 1, diagnostics);
        var parser = new ExpressionParser(tokens, 0, diagnostics);
        var syntax = diagnostics.HasErrors ? null : parser.Parse();
        if (syntax is not null && !diagnostics.HasErrors && tokens[parser.Position].Kind != TokenKind.End)
            diagnostics.Error($"Expected the condition to end before {tokens[parser.Position]}.", tokens[parser.Position].Span);
        if (syntax is null || diagnostics.HasErrors)
            throw new FormatException(string.Join(" ", diagnostics.Items.Where(d => d.IsError).Select(d => d.Message)));

        var unknown = new List<string>();
        var scope = new CompileScope(
            new Dictionary<string, ConstantValue>(),
            _ => new FamTarget(Type: null),
            name => registry is not null && registry.TryGetMessageType(name, out var type) ? type : null,
            [],
            name =>
            {
                unknown.Add(name);
                return $"There's no message type called {name}.";
            });

        // Compiling it for Message finds names of types that don't exist, which would otherwise never match.
        try
        {
            ExpressionCompiler.Compile<bool>(syntax, scope, typeof(Message));
        }
        catch (BindException exception) when (unknown.Count > 0)
        {
            throw new FormatException(exception.Message, exception);
        }
        catch (BindException)
        {
            // It reads members that Message doesn't have, which other types may.
        }
        return new MessageCondition(text, syntax, scope);
    }

    /// <summary>Whether a message matches.</summary>
    public bool Matches(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var compiled = _compiled.GetValue(message.GetType(), type =>
        {
            try
            {
                return new StrongBox<Func<Message, bool>?>(ExpressionCompiler.Compile<bool>(_syntax, _scope, type));
            }
            catch (BindException)
            {
                return new StrongBox<Func<Message, bool>?>(null);
            }
        }).Value;
        if (compiled is null)
            return false;
        try
        {
            return compiled(message);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
