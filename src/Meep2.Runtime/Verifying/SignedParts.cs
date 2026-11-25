using Meep2.SDK.Messages;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// Whether something a pipeline read from a signed request came from a part the signature covers, so it can't have
/// been changed on the way. <c>Authorise</c> and <c>Revoke</c> ask this of what they act on: the authorising
/// credential's signature covers the request, but a header the client didn't list in <c>Signature-Input</c> could be
/// changed by anyone between the two, on plain HTTP, and the grant sent to someone else.
/// </summary>
/// <remarks>
/// The signed request is the nearest signed HTTP request among the message's ancestors. Its covered parts are its body
/// (when <c>content-digest</c> is covered, which <c>Verify</c> checks against the body), its address (when
/// <c>@target-uri</c> or <c>@query</c> is), each header it lists, and <c>Meep-Credential</c>, which the signature
/// binds by being made with that credential's key. Text found in a covered part is fine, as is text found nowhere in
/// the request, which the pipeline got from somewhere else, such as a file. Only text found in an uncovered header and
/// in no covered part is refused.
/// </remarks>
internal static class SignedParts
{
    /// <summary>
    /// Why <paramref name="text"/>, read for <paramref name="message"/>, could have been changed on the way, or null if
    /// it couldn't.
    /// </summary>
    /// <param name="message">The message being acted on, whose ancestors include the request.</param>
    /// <param name="text">What was read: a credential's, certificate's or grant's base64, or a permission's name.</param>
    /// <param name="whole">True to match a header's whole value, for short text such as a name; false to find it inside one, ignoring white space, for base64.</param>
    /// <param name="what">What it is, for saying so: "the credential to grant to".</param>
    public static string? WhyNotCovered(Message message, string text, bool whole, string what)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(text)
            || Ancestry.Of(message).OfType<IHttpRequest>().FirstOrDefault(RequestSignature.IsSigned) is not { } request
            || RequestSignature.Covered(request) is not { } covered)
        {
            return null;
        }
        var wanted = whole ? text.Trim() : Squeezed(text);
        bool Holds(string? value) => value is not null && (whole ? string.Equals(value.Trim(), wanted, StringComparison.Ordinal) : Squeezed(value).Contains(wanted, StringComparison.Ordinal));
        bool Inside(string? value) => value is not null && (whole ? value.Contains(wanted, StringComparison.Ordinal) : Squeezed(value).Contains(wanted, StringComparison.Ordinal));

        if (covered.Contains("content-digest") && Inside((request as IHasText)?.Text))
            return null;
        if ((covered.Contains("@target-uri") || covered.Contains("@query")) && (Inside(request.Url) || Inside(Uri.UnescapeDataString(request.Url))))
            return null;
        string? uncovered = null;
        foreach (var (name, value) in request.Headers)
        {
            var isCovered = name.Equals("Meep-Credential", StringComparison.OrdinalIgnoreCase)
                || covered.Contains(name.ToLowerInvariant());
            if (isCovered && Inside(value))
                return null;
            if (!isCovered && Holds(value))
                uncovered ??= name;
        }
        return uncovered is null
            ? null
            : $"It took {what} from the request's {uncovered} header, which the request's signature doesn't cover, so it could have been changed on the way. Read it from the body, or have the client sign {uncovered.ToLowerInvariant()} too.";
    }

    // Text without white space, as base64 is whether it's on one line or wrapped in PEM.
    private static string Squeezed(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
