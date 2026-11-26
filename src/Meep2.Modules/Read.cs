using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Modules;

/// <summary>
/// Reads a text file once and emits its contents as <see cref="FileText"/>. In Meeplang: <c>Read "~/notes.txt"</c>.
/// A leading <c>~</c> means your home folder; other relative paths start from the current folder. The pipeline must
/// hold <c>meep.files.read</c> for the file.
/// </summary>
[Verb("Read")]
[ChecksPermission(MeepPermissions.FilesRead, "the file it reads")]
[Emits(typeof(FileText))]
public sealed class Read(ReadParameters parameters) : ASource
{
    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var path = Paths.Expand(parameters.Path);
        context.DemandFile(MeepPermissions.FilesRead, null, path);
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
