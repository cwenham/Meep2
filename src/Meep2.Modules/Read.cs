using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Reads a text file once and emits its contents as <see cref="FileText"/>. In Meeplang: <c>Read "~/notes.txt"</c>.
/// A leading <c>~</c> means your home folder; other relative paths start from the current folder.
/// </summary>
[Verb("Read")]
[Emits(typeof(FileText))]
public sealed class Read(ReadParameters parameters) : ASource
{
    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var path = Paths.Expand(parameters.Path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        context.Emit(new FileText(path, text));
    }
}

/// <summary>Parameters for <see cref="Read"/>.</summary>
public sealed record ReadParameters
{
    /// <summary>The file to read.</summary>
    [Positional]
    public required string Path { get; init; }
}
