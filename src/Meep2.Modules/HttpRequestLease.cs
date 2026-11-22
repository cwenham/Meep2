using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Microsoft.AspNetCore.Http;

namespace Meep2.Modules;

/// <summary>A request from <see cref="HttpListen"/> waiting for its answer.</summary>
/// <remarks>The bytes of its answer are counted for the Listen, whichever module sends it.</remarks>
internal sealed class HttpRequestLease(HttpContext http, string url, int defaultStatus, string defaultReply, ModuleContext listen, Tally? from = null) : Lease
{
    private readonly Lock _answering = new();
    private Task? _answer;
    private bool _abandoned;

    public string Url => url;

    /// <summary>Sends the response, from bytes or a stream. Returns false if the request has already been answered.</summary>
    public async Task<bool> AnswerAsync(int status, string? contentType, ReadOnlyMemory<byte> body, Stream? stream, CancellationToken cancellationToken)
    {
        Task answer;
        lock (_answering)
        {
            if (_abandoned || IsClosed)
                throw new IOException($"the request for {url} has closed");
            if (_answer is not null)
                return false;
            answer = _answer = WriteAsync(status, contentType, body, stream, cancellationToken);
        }
        await answer.ConfigureAwait(false);
        return true;
    }

    private async Task WriteAsync(int status, string? contentType, ReadOnlyMemory<byte> body, Stream? stream, CancellationToken cancellationToken)
    {
        var response = http.Response;
        response.StatusCode = status;
        if (contentType is not null)
            response.ContentType = contentType;
        // A reply is counted for where the request came from: 400 to 499 as rejected, 500 and over as failed.
        from?.Count(sent: 1);
        from?.Status(status);
        if (stream is null)
        {
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            listen.CountBytes(0, body.Length);
            from?.Count(bytesSent: body.Length);
        }
        else
        {
            await stream.CopyToAsync(new CountingStream(response.Body, (received, sent) =>
            {
                listen.CountBytes(received, sent);
                from?.Count(bytesReceived: received, bytesSent: sent);
            }), cancellationToken).ConfigureAwait(false);
        }
        await response.CompleteAsync().ConfigureAwait(false);
    }

    // Nothing answered: send the default reply before closing (Q50).
    protected override async ValueTask OnFinishedAsync()
    {
        try
        {
            await AnswerAsync(defaultStatus, Bodies.PlainText, Encoding.UTF8.GetBytes(defaultReply), null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Closed already, or the client went away.
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The client went away.
        }
        await CloseAsync().ConfigureAwait(false);
    }

    // Kestrel reuses the request's objects once Listen lets go of it, so an answer still being written is finished
    // first, and a request nobody answered is abandoned.
    protected override async ValueTask OnClosedAsync()
    {
        Task? answer;
        lock (_answering)
        {
            answer = _answer;
            _abandoned = answer is null;
        }
        if (answer is null)
        {
            http.Abort();
            return;
        }
        try
        {
            await answer.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // Whoever was answering has been told.
        }
    }
}
