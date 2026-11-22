using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Compilation;

/// <summary>An expression can't be compiled, at least not for the message type being tried.</summary>
internal sealed class BindException(string message, SourceSpan span) : Exception(message)
{
    public SourceSpan Span { get; } = span;
}
