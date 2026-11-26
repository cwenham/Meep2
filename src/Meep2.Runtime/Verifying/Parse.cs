using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Runtime;

/// <summary>
/// Reads text as a credential, certificate, permission, grant or authority's key, and emits it as that message, for verbs that take
/// only typed messages, such as <c>Authorise</c> and <c>Revoke</c> (Q293). Text that's none of them becomes an error
/// saying what it found. In Meeplang: <c>Parse from=fam.Listen.Json.credential</c>.
/// </summary>
/// <remarks>
/// It reads with the same reader as <c>Verify</c> (<see cref="SecurityText"/>), so the two never disagree about what
/// text is. A message that's already one of the five is passed on as it is. A <c>Credential</c> it emits is only read,
/// not checked: <c>Verify</c> checks one. With <c>kind=</c> it emits only that kind, so the modules below it need take
/// only that type: <c>Parse kind=credential</c> above <c>Authorise permission="…"</c>.
/// </remarks>
[Verb("Parse", Inflection = "Text")]
[Emits(typeof(Credential), When = nameof(ParseParameters.MayBeCredential))]
[Emits(typeof(Certificate), When = nameof(ParseParameters.MayBeCertificate))]
[Emits(typeof(Permission), When = nameof(ParseParameters.MayBePermission))]
[Emits(typeof(Grant), When = nameof(ParseParameters.MayBeGrant))]
[Emits(typeof(AuthorityKey), When = nameof(ParseParameters.MayBeAuthority))]
public sealed class ParseText(ParseParameters parameters) : AInflection<Message>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }
        if (parameters.From is null && message is Credential or Certificate or Permission or Grant or AuthorityKey)
        {
            if (WrongKind(message) is { } wrong)
                context.EmitError(wrong);
            else
                context.Emit(message);
            return ValueTask.CompletedTask;
        }

        string text;
        try
        {
            text = parameters.From is { } from ? from.Evaluate(message) ?? "" : (message as IHasText)?.Text ?? "";
        }
        catch (EvaluationException exception)
        {
            context.EmitError($"There's nothing to parse: from= can't be worked out for this message. {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            context.EmitError(parameters.From is null
                ? $"There's nothing to parse: a {message.GetType().Name} has no text. Say where it is with from=."
                : "There's nothing to parse: from= gave nothing.");
            return ValueTask.CompletedTask;
        }

        Message parsed;
        try
        {
            parsed = parameters.Kind switch
            {
                ParseKind.Credential => SecurityText.Read<Credential>(text),
                ParseKind.Certificate => SecurityText.Read<Certificate>(text),
                ParseKind.Permission => SecurityText.Read<Permission>(text),
                ParseKind.Grant => SecurityText.Read<Grant>(text),
                ParseKind.Authority => SecurityText.Read<AuthorityKey>(text),
                _ => SecurityText.Read(text),
            };
        }
        catch (FormatException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }
        context.Emit(parsed);
        return ValueTask.CompletedTask;
    }

    // Why a message that's already typed isn't the kind asked for, or null if it is.
    private string? WrongKind(Message message) => parameters.Kind switch
    {
        ParseKind.Any => null,
        ParseKind.Credential when message is Credential => null,
        ParseKind.Certificate when message is Certificate => null,
        ParseKind.Permission when message is Permission => null,
        ParseKind.Grant when message is Grant => null,
        ParseKind.Authority when message is AuthorityKey => null,
        _ => $"This is a {message.GetType().Name}, not a {parameters.Kind.ToString().ToLowerInvariant()}.",
    };
}

/// <summary>Parameters for <see cref="ParseText"/>.</summary>
public sealed record ParseParameters
{
    /// <summary>
    /// The text to read, from the message, such as <c>fam.Listen.Json.credential</c>. Without it, the message's own text.
    /// </summary>
    public Computed<string>? From { get; init; }

    /// <summary>
    /// What the text must be: <c>any</c> (the default), <c>credential</c>, <c>certificate</c>, <c>permission</c>,
    /// <c>grant</c> or <c>authority</c>. Anything else becomes an error.
    /// </summary>
    public ParseKind Kind { get; init; }

    /// <summary>Whether it may emit a credential, for the loader's checks.</summary>
    public bool MayBeCredential => Kind is ParseKind.Any or ParseKind.Credential;

    /// <summary>Whether it may emit a certificate.</summary>
    public bool MayBeCertificate => Kind is ParseKind.Any or ParseKind.Certificate;

    /// <summary>Whether it may emit a permission.</summary>
    public bool MayBePermission => Kind is ParseKind.Any or ParseKind.Permission;

    /// <summary>Whether it may emit a grant.</summary>
    public bool MayBeGrant => Kind is ParseKind.Any or ParseKind.Grant;

    /// <summary>Whether it may emit an authority's key.</summary>
    public bool MayBeAuthority => Kind is ParseKind.Any or ParseKind.Authority;
}

/// <summary>What <c>Parse</c> reads text as.</summary>
public enum ParseKind
{
    /// <summary>Whatever it is.</summary>
    Any,

    /// <summary>An OpenSSH user certificate.</summary>
    Credential,

    /// <summary>An X.509 certificate, PEM or DER in base64.</summary>
    Certificate,

    /// <summary>A permission's name, or a <c>#permission</c> line.</summary>
    Permission,

    /// <summary>A grant, as <c>meep-grant-v02 base64</c> (or v01).</summary>
    Grant,

    /// <summary>An SSH certificate authority's public key, as a <c>.pub</c> file has it (Q303).</summary>
    Authority,
}
