using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// What a <c>Verify</c> is given to verify for one message: the message itself if it's a credential, certificate or
/// permission, or else the text <c>from=</c> reads from it, or its own text. Which of the three it is chooses the
/// inflection, so the choice is made from the text's form alone, cheaply, and the text is read in full only once
/// chosen.
/// </summary>
internal static class VerifySubject
{
    // The last choice made, since each of Verify's inflections asks in turn about the same message.
    [ThreadStatic]
    private static Choice? t_last;

    /// <summary>Which kind of thing the message gives to verify. Anything that's neither a certificate nor a permission
    /// goes to <c>Verify.Credential</c>, which says why it can't be read if it can't.</summary>
    public static VerificationKind KindOf(VerifyParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(message);
        if (t_last is { } last && ReferenceEquals(last.Message, message) && ReferenceEquals(last.Parameters, parameters))
            return last.Kind;
        var kind = parameters.From is null ? KindOfMessage(message) : TextOf(parameters.From, message) is { } text ? KindOfText(text) : VerificationKind.Credential;
        t_last = new Choice(message, parameters, kind);
        return kind;
    }

    /// <summary>
    /// Reads what the message gives to verify, as <typeparamref name="T"/>, or says why it can't be read.
    /// </summary>
    public static SubjectRead<T> Read<T>(VerifyParameters parameters, Message message)
        where T : Message
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(message);
        string text;
        if (parameters.From is null)
        {
            if (message is T subject)
                return new SubjectRead<T>(subject, null) { IsMessage = true };
            if (message is not IHasText hasText)
                return new SubjectRead<T>(null, $"A {message.GetType().Name} isn't a credential, certificate or permission, and has no text to read one from. Say where it is with from=.");
            text = hasText.Text;
        }
        else
        {
            try
            {
                text = parameters.From.Evaluate(message) ?? "";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Meeplang's expressions throw EvaluationException; one made in code, such as a dictionary lookup, may throw anything.
                return new SubjectRead<T>(null, $"There's nothing to verify: from= can't be worked out for this message. {exception.Message}");
            }
        }
        if (string.IsNullOrWhiteSpace(text))
            return new SubjectRead<T>(null, parameters.From is null ? "There's nothing to verify: the message's text is empty." : "There's nothing to verify: from= gave nothing.");
        try
        {
            return new SubjectRead<T>(SecurityText.Read<T>(text), null);
        }
        catch (FormatException exception)
        {
            return new SubjectRead<T>(null, exception.Message);
        }
    }

    private static VerificationKind KindOfMessage(Message message) => message switch
    {
        Certificate => VerificationKind.Certificate,
        Permission => VerificationKind.Permission,
        Credential => VerificationKind.Credential,
        IHasText text => KindOfText(text.Text),
        _ => VerificationKind.Credential,
    };

    private static string? TextOf(Computed<string> from, Message message)
    {
        try
        {
            return from.Evaluate(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    // What text is, by the SDK's one reader (Q293). Anything that isn't a certificate or a permission, a grant
    // included, goes to Verify.Credential, which says why it can't be read as a credential.
    internal static VerificationKind KindOfText(string text) => SecurityText.KindOf(text) switch
    {
        SecurityTextKind.Certificate => VerificationKind.Certificate,
        SecurityTextKind.Permission => VerificationKind.Permission,
        _ => VerificationKind.Credential,
    };

    private sealed record Choice(Message Message, VerifyParameters Parameters, VerificationKind Kind);
}

/// <summary>What a <c>Verify</c> read to verify, or why it couldn't read it.</summary>
/// <param name="Subject">What was read, or null.</param>
/// <param name="Problem">Why it couldn't be read, or null.</param>
internal sealed record SubjectRead<T>(T? Subject, string? Problem)
    where T : Message
{
    /// <summary>Whether the subject is the message itself, rather than read from text.</summary>
    public bool IsMessage { get; init; }
}
