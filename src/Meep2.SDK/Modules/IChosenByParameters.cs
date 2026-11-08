using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// For an inflection that can only do some of what its verb's parameters can ask, such as an HTTP fetch, which can't
/// fetch a file. When a verb has several inflections, the parameters then choose between them (Q22).
/// </summary>
/// <typeparam name="TParameters">The inflection's parameters record.</typeparam>
/// <remarks>
/// When the parameters are the same for every message, the answer is worked out once, when the pipeline loads, and
/// an inflection that refuses them is left out. A pipeline with none left fails to load, with each inflection's
/// reason. When a parameter is worked out for each message, such as a URL template, the answer is worked out for
/// each message, and the message goes to the first inflection that takes it.
/// </remarks>
public interface IChosenByParameters<in TParameters>
    where TParameters : class
{
    /// <summary>Why this inflection can't do what the parameters ask, such as "needs an http or https address", or null if it can.</summary>
    /// <param name="parameters">The inflection's parameters.</param>
    /// <param name="message">
    /// The message being routed, for working out parameters that depend on it. Look at the parameters rather than the
    /// message itself: the runtime chooses by message type separately, and when the pipeline loads this is a stand-in.
    /// </param>
    static abstract string? WhyNot(TParameters parameters, Message message);
}
