using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>An HTTP response with a success status, as Fetch's HTTP inflection emits it.</summary>
/// <remarks>With <c>stream=true</c>, the body is left unread: <see cref="Body"/> and <see cref="Text"/> are empty, and
/// one module can read it with <see cref="OpenBody"/>. The response carries a lease that keeps the connection open
/// until then.</remarks>
public sealed record HttpResponse : Message, IHasText, IHasBody
{
    /// <summary>The address that was fetched.</summary>
    public required string Url { get; init; }

    /// <summary>The status code, such as 200.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The status code's description, such as "OK".</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>The media type of the body, such as <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>The response headers. Names are case-insensitive; repeated headers are joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body as bytes.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The body as text, decoded with the response's character set (UTF-8 if it doesn't say).</summary>
    public required string Text { get; init; }

    /// <inheritdoc/>
    public bool IsStreamed => Streamed is not null;

    internal StreamedBody? Streamed { get; init; }

    /// <inheritdoc/>
    public Stream OpenBody() => Streamed?.Open() ?? Bodies.Open(Body);

    /// <summary>Prints the address and status, leaving out the body.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Url = ").Append(Url)
            .Append(", StatusCode = ").Append(StatusCode)
            .Append(", ContentType = ").Append(ContentType)
            .Append(", Length = ").Append(Body.Length);
        return true;
    }
}

/// <summary>
/// A request that reached Listen's HTTP inflection. It carries a lease on the waiting connection, which a Send without
/// an address answers through. If nothing answers it by the time it and every message made from it have left the
/// pipeline, the runtime sends Listen's default reply (Q50).
/// </summary>
/// <remarks>With <c>stream=true</c> on Listen, the body is left unread: <see cref="Body"/> and <see cref="Text"/> are
/// empty, and one module can read it with <see cref="OpenBody"/> (Q51).</remarks>
public sealed record HttpRequest : Message, IHasText, IHttpRequest
{
    /// <summary>The method, such as GET or POST.</summary>
    public required string Method { get; init; }

    /// <summary>The whole address that was requested.</summary>
    public required string Url { get; init; }

    /// <summary>The path part of the address, such as <c>/quote/7</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The query's values by name. A name given more than once has its values joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Query { get; init; }

    /// <summary>The request headers. Names are case-insensitive; repeated headers are joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The media type of the body, such as <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>The body as bytes, empty if there is none or it's streamed.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The body as text, decoded with the request's character set (UTF-8 if it doesn't say).</summary>
    public string Text { get; init; } = "";

    /// <summary>The address and port the request came from.</summary>
    public string? RemoteAddress { get; init; }

    /// <inheritdoc/>
    public bool IsStreamed => Streamed is not null;

    internal StreamedBody? Streamed { get; init; }

    /// <inheritdoc/>
    public Stream OpenBody() => Streamed?.Open() ?? Bodies.Open(Body);

    /// <summary>Prints the method and address, leaving out the body.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Method = ").Append(Method)
            .Append(", Url = ").Append(Url)
            .Append(", Length = ").Append(Body.Length);
        return true;
    }
}

/// <summary>An HTTP response whose status wasn't a success, as Fetch's HTTP inflection emits it.</summary>
public sealed record HttpErrorMessage : ErrorMessage
{
    /// <summary>The address that was fetched.</summary>
    public required string Url { get; init; }

    /// <summary>The status code, such as 404.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The body as text, which often explains the error.</summary>
    public string? Text { get; init; }
}
