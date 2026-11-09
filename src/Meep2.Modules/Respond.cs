using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Answers the request a message came from, through its lease: an HTTP response for a request from Listen.HTTP, or
/// text for a connection to Listen.TCP. A passthrough module: messages carry on downstream straight away.
/// In Meeplang: <c>Respond "Hello, {msg.Query['name']}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without a value, Respond sends the message itself: a body it has, such as a request's or a fetched response's
/// (streamed through if it was streamed), or else its text.
/// </para>
/// <para>
/// An error is answered with <c>error-reply</c>, "Something went wrong." unless set, and status 500 unless
/// <c>error-status</c> says otherwise. These are worked out from the error, so <c>error-reply="Failed: {msg.Error}"</c>
/// includes its description. <c>details=true</c> sends everything known about it instead, which can show addresses
/// and exception text to whoever asked (Q98).
/// </para>
/// <para>
/// An error Respond answers is still logged, unless a module after it handles the error, but it doesn't count towards
/// stopping the pipeline, so bad requests can't stop a server (Q100).
/// </para>
/// <para>
/// An HTTP request can only be answered once; trying again is an error. A TCP connection can be written to any
/// number of times. Each write ends the way the connection's messages do (a line, or Listen's <c>end</c>) unless
/// <c>end</c> says otherwise, and <c>final=true</c> closes the connection after writing.
/// </para>
/// </remarks>
[Verb("Respond")]
public sealed class Respond(RespondParameters parameters) : APassthrough<Message>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        var leases = message.Leases.Where(lease => lease is HttpRequestLease or TcpConnectionLease).ToList();
        if (leases.Count == 0)
        {
            // An error with no request behind it, such as one from Listen itself, is passed on in Respond's words.
            context.EmitError(message is ErrorMessage error
                ? $"Respond had no request to answer with this error: {error.Error}"
                : "Respond needs a message that came from a request, through Listen, but this one didn't.");
            return;
        }

        string? text;
        int status;
        string? contentType;
        try
        {
            if (message is ErrorMessage error)
            {
                text = parameters.Details ? Describe(error) : parameters.ErrorReply.Evaluate(message);
                status = parameters.ErrorStatus?.Evaluate(message) ?? 500;
                contentType = Bodies.PlainText;
            }
            else
            {
                text = parameters.Body?.Evaluate(message);
                status = parameters.Status?.Evaluate(message) ?? 200;
                contentType = parameters.ContentType?.Evaluate(message);
            }
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        foreach (var lease in leases)
        {
            try
            {
                switch (lease)
                {
                    case HttpRequestLease http:
                        if (!await AnswerAsync(http, message, text, status, contentType, cancellationToken).ConfigureAwait(false))
                        {
                            context.EmitError($"The request for {http.Url} has already been answered, so Respond couldn't answer it again.");
                            continue;
                        }
                        break;
                    case TcpConnectionLease tcp:
                        await tcp.WriteAsync(text ?? message.ToText(), parameters.End ?? tcp.Ending, parameters.Final, cancellationToken).ConfigureAwait(false);
                        break;
                }
                if (message is ErrorMessage answered)
                    context.Answered(answered);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                context.EmitError($"Couldn't respond to {lease}: {exception.Message}", exception);
            }
        }
    }

    private static async Task<bool> AnswerAsync(HttpRequestLease http, Message message, string? text, int status, string? contentType, CancellationToken cancellationToken)
    {
        if (text is not null)
            return await http.AnswerAsync(status, contentType ?? Bodies.PlainText, Encoding.UTF8.GetBytes(text), null, cancellationToken).ConfigureAwait(false);

        switch (message)
        {
            case IHasBody { IsStreamed: true } streamed:
            {
                await using var body = streamed.OpenBody();
                return await http.AnswerAsync(status, contentType ?? streamed.ContentType, default, body, cancellationToken).ConfigureAwait(false);
            }
            case HttpRequest request:
                return await http.AnswerAsync(status, contentType ?? request.ContentType, request.Body, null, cancellationToken).ConfigureAwait(false);
            case HttpResponse response:
                return await http.AnswerAsync(status, contentType ?? response.ContentType, response.Body, null, cancellationToken).ConfigureAwait(false);
            case JsonMessage json:
                return await http.AnswerAsync(status, contentType ?? "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Text), null, cancellationToken).ConfigureAwait(false);
            default:
                return await http.AnswerAsync(status, contentType ?? Bodies.PlainText, Encoding.UTF8.GetBytes(message.ToText()), null, cancellationToken).ConfigureAwait(false);
        }
    }

    // Everything known about an error: its description, and the exception behind it with its stack trace.
    private static string Describe(ErrorMessage error) =>
        error.Exception is { } exception ? $"{error.Error}\n\n{exception}" : error.Error;
}

/// <summary>Parameters for <see cref="Respond"/>.</summary>
public sealed record RespondParameters
{
    /// <summary>What to send, worked out for each message. Without it, Respond sends the message itself.</summary>
    [Positional]
    public Computed<string>? Body { get; init; }

    /// <summary>The HTTP status: 200 unless set.</summary>
    public Computed<int>? Status { get; init; }

    /// <summary>The HTTP media type. Without it, the message's own, or text.</summary>
    public Computed<string>? ContentType { get; init; }

    /// <summary>What to send for an error, worked out from the error (Q98).</summary>
    [ForErrors]
    public Computed<string> ErrorReply { get; init; } = Computed<string>.Constant("Something went wrong.");

    /// <summary>The HTTP status for an error: 500 unless set.</summary>
    [ForErrors]
    public Computed<int>? ErrorStatus { get; init; }

    /// <summary>
    /// Sends everything known about an error, instead of <see cref="ErrorReply"/>: its description, and the exception
    /// behind it. Meant for tools and testing, since it can show internal details to whoever asked.
    /// </summary>
    public bool Details { get; init; }

    /// <summary>
    /// What to write after the text on a TCP connection. Without it, what ends the connection's own messages: a line
    /// break, or Listen's <c>end</c>.
    /// </summary>
    public string? End { get; init; }

    /// <summary>Closes a TCP connection after writing to it.</summary>
    public bool Final { get; init; }
}
