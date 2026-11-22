namespace Meep2.Meeplang.Compilation;

/// <summary>A module that <c>fam</c> can name, and the one message type it's known to emit, if there's exactly one.</summary>
internal sealed record FamTarget(Type? Type);
