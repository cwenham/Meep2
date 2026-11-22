using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>A response Fetch streamed, kept open until its body has been read and its messages have left.</summary>
internal sealed class OpenResponse(HttpResponseMessage response) : Lease
{
    protected override ValueTask OnClosedAsync()
    {
        response.Dispose();
        return ValueTask.CompletedTask;
    }
}
