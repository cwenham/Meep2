using Meep2.Meeplang.Syntax;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Compilation;

/// <summary>A compiled expression the loader can check against message types before the pipeline runs.</summary>
internal interface ICheckedComputed
{
    /// <summary>The expression as written.</summary>
    string Text { get; }

    /// <summary>Whether the expression fits messages of this type.</summary>
    bool AppliesTo(Type messageType);

    /// <summary>Why the expression doesn't fit messages of this type, and where, or null if it does.</summary>
    (string Problem, SourceSpan Span)? ProblemWith(Type messageType);

    /// <summary>
    /// Which messages the parameter is worked out for: null for all, true for errors only, false for the rest
    /// (<see cref="ForErrorsAttribute"/>).
    /// </summary>
    bool? ForErrors { get; set; }
}
