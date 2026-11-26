using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;

namespace Meep2.Meeplang.Hosting;

/// <summary>A parsed pipeline file, as <see cref="Parse"/> emits it.</summary>
/// <param name="Syntax">The parsed file, including any warnings.</param>
public sealed record ParsedPipeline(PipelineSyntax Syntax) : Message
{
    /// <summary>
    /// The verbs this pipeline can use, when it needs its own, as a pipeline with plugins does. Null means the ones
    /// <see cref="Build"/> was given.
    /// </summary>
    public ModuleRegistry? Registry { get; init; }

    /// <summary>
    /// What keeps the pipeline's plugins loaded. The pipeline lets go of them when it's finished with, so they can
    /// unload.
    /// </summary>
    public IReadOnlyList<IDisposable> Holds { get; init; } = [];

    /// <summary>Warnings found after parsing, such as a plugin loaded without a signature in developer mode.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
}

/// <summary>A pipeline ready to run, as <see cref="Build"/> emits it.</summary>
/// <param name="Pipeline">The pipeline.</param>
/// <param name="Warnings">Warnings from parsing and loading.</param>
public sealed record BuiltPipeline(Pipeline Pipeline, IReadOnlyList<Diagnostic> Warnings) : Message
{
    /// <summary>What keeps the pipeline's plugins loaded, let go of once the pipeline has finished.</summary>
    public IReadOnlyList<IDisposable> Holds { get; init; } = [];

    /// <summary>The verbs and message types the pipeline was built with, its plugins' included, for conditions on its messages.</summary>
    public ModuleRegistry? Registry { get; init; }

    /// <summary>Builds the same pipeline again, to restart it (Q112). Null if it can't be.</summary>
    public Func<Pipeline>? Rebuild { get; init; }

    /// <summary>
    /// The pipeline as one that can be changed while it runs (goal 4), with the text it runs from now. Null for a
    /// pipeline built some other way.
    /// </summary>
    public Editing.LivePipeline? Live { get; init; }

    /// <summary>This, with a newly built pipeline in place of the one that ran.</summary>
    internal BuiltPipeline Rebuilt() => new(Rebuild!(), Warnings) { Holds = Holds, Registry = Registry, Rebuild = Rebuild, Live = Live };
}

/// <summary>A pipeline that failed, which <see cref="Host"/> is about to restart, as its error policy says (Q112).</summary>
/// <param name="Name">The pipeline's name.</param>
/// <param name="Reason">Why it stopped, as in "it threw: …" or the error policy's reason.</param>
/// <param name="Delay">How long until it starts again.</param>
/// <param name="Restart">Which restart this is, counting from 1.</param>
/// <param name="Policy">The policy restarting it.</param>
public sealed record PipelineRestarting(string Name, string Reason, TimeSpan Delay, int Restart, PipelineRestart Policy)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var window = Policy.Within is { } within ? $" in {Durations.Format(within)}" : "";
        return $"{Name} was stopped, because {Reason}. Restarting it in {Durations.Format(Delay)} (restart {Restart} of at most {Policy.Times}{window}).";
    }
}

/// <summary>
/// A pipeline's text from somewhere other than a file on this machine, such as a URL, Git or standard input, as the
/// bootstrap's first stage emits it. Its includes are read from the same place (goal 7).
/// </summary>
/// <param name="Path">Where it came from, used in diagnostics and as the pipeline's name.</param>
/// <param name="Text">The pipeline's text.</param>
public sealed record PipelineText(string Path, string Text) : Message, IHasText, IHasPath
{
    /// <summary>Reads its includes from where it came from, or null to read them from this machine's disk.</summary>
    public IIncludeReader? Includes { get; init; }

    /// <summary>Who's trusted it: the key that signed it, or the pin it matched. Null for a file of your own.</summary>
    public string? TrustedBecause { get; init; }

    /// <summary>
    /// The pipeline's own credential, as where it came from earns it (Q252): its signer's, the owner's for one whose
    /// address pins it, or none. Null for the host's own default, the owner's.
    /// </summary>
    public PipelineCredential? Credential { get; init; }
}

/// <summary>Problems that stopped a pipeline file loading.</summary>
public sealed record MeeplangErrorMessage : ErrorMessage
{
    /// <summary>Every error and warning found.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The error for a set of diagnostics, described by its errors.</summary>
    public static MeeplangErrorMessage From(IReadOnlyList<Diagnostic> diagnostics) => new()
    {
        Error = string.Join(Environment.NewLine, diagnostics.Where(d => d.IsError)),
        Diagnostics = diagnostics,
    };
}

/// <summary>A pipeline that its error policy stopped, as <see cref="Host"/> reports it.</summary>
public sealed record PipelineStoppedMessage : ErrorMessage
{
    /// <summary>Why the error policy stopped it.</summary>
    public required string Reason { get; init; }
}
