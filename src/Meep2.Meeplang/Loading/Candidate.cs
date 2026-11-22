using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Loading;

/// <summary>One inflection a module might use, with its parameters filled in.</summary>
internal sealed class Candidate(ModuleRegistration registration, BoundParameters parameters)
{
    public ModuleRegistration Registration { get; } = registration;

    public BoundParameters Parameters { get; } = parameters;

    /// <summary>The inflection's input type. Only modules that handle messages have candidates that are asked.</summary>
    public Type InputType => Registration.InputType ?? typeof(Message);

    /// <summary>
    /// Asked for each message, when the inflection's parameters are worked out per message. Null when they're the
    /// same for every message, because then they were checked when the pipeline loaded.
    /// </summary>
    public Func<Message, string?>? WhyNot { get; set; }
}
