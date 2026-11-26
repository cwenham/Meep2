using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// What Send's TCP and WebSocket inflections share: errors left alone, the address and text worked out for each
/// message, a connection shared by address, and sending through it with retries and a timeout. Each inflection says
/// how it checks its address and makes its connection.
/// </summary>
/// <typeparam name="TParameters">The inflection's parameters.</typeparam>
public abstract class ConnectionSend<TParameters> : APassthrough<Message>, IAsyncDisposable, ISendsTo
    where TParameters : SendConnectionParameters
{
    private protected ConnectionSend(TParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = parameters;
    }

    /// <inheritdoc/>
    public Computed<string> Address => Parameters.Url;

    private protected TParameters Parameters { get; }

    /// <summary>The connections this module has joined, by address.</summary>
    private protected Connections Joined { get; } = new();

    /// <inheritdoc/>
    protected sealed override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage error)
        {
            context.LeftAlone(error);
            return;
        }
        string url;
        string? text;
        try
        {
            url = Parameters.Url.Evaluate(message);
            text = Parameters.Text?.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        if (ClientFor(url, message, context) is not { } client)
            return;
        await Connections.SendAsync(client, message, text, End, Parameters, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The connection to send through, joined if this module hasn't used it before; or null, having emitted an error
    /// saying why, if the address isn't one this inflection takes. It checks that <paramref name="message"/> may send
    /// there (<c>meep.network.send</c>), throwing <see cref="SDK.Security.PermissionDeniedException"/> if not.
    /// </summary>
    private protected abstract SharedClient? ClientFor(string url, Message message, ModuleContext context);

    /// <summary>What ends each message written, or null for the connection's own way.</summary>
    private protected virtual string? End => null;

    /// <summary>Leaves the connections it used, which close once nothing uses them.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Joined.DisposeAsync();
    }
}
