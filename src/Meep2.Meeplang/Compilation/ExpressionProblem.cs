using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Compilation;

/// <summary>Why an expression doesn't fit a message type, and where in the expression.</summary>
/// <param name="Message">What's wrong, as a diagnostic says it.</param>
/// <param name="Span">Where.</param>
internal sealed record ExpressionProblem(string Message, SourceSpan Span);
