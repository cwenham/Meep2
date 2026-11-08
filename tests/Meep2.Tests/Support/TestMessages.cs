using Meep2.SDK.Messages;

namespace Meep2.Tests.Support;

public sealed record Number(int Value) : Message;

public sealed record Word(string Value) : Message;

/// <summary>A message with a double, as a C# module might emit.</summary>
public sealed record Reading(double Value) : Message;
