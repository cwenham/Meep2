using Meep2.SDK.Messages;

namespace Meep2.Tests.Support;

public sealed record Number(int Value) : Message;

public sealed record Word(string Value) : Message;
