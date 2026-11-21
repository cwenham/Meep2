using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Emits a <see cref="Tick"/> straight away, then again every period, until stopped or until it has emitted
/// <c>count</c> ticks. In Meeplang: <c>Interval 1m</c> or <c>Interval 500ms count=3</c>. Ticks are numbered from 1, or
/// from <c>count-start</c>, as in <c>Interval 1s count-start=0</c>.
/// </summary>
[Verb("Interval")]
[Emits(typeof(Tick))]
public sealed class Interval(IntervalParameters parameters) : ASource, IReconfigurable<IntervalParameters>
{
    private volatile IntervalParameters _parameters = parameters;
    private volatile PeriodicTimer? _timer;

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_parameters.Period);
        _timer = timer;
        for (long count = 1; ; count++)
        {
            var current = _parameters;
            context.Emit(new Tick(current.CountStart + count - 1) { Payload = current.Payload });
            if (count >= current.Count)
                return;
            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// Takes a new period, count, count start and payload while it runs, carrying on its count (Q121). A new count it
    /// has already reached ends it at the next tick, and a new count start numbers the ticks after it from there.
    /// </summary>
    public bool ParametersChanged(IntervalParameters old, IntervalParameters next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        _parameters = next;
        if (next.Period != old.Period && _timer is { } timer)
        {
            try
            {
                timer.Period = next.Period;
            }
            catch (ObjectDisposedException)
            {
                // It has finished meanwhile.
            }
        }
        return true;
    }
}

/// <summary>Parameters for <see cref="Interval"/>.</summary>
public sealed record IntervalParameters
{
    /// <summary>How long between ticks.</summary>
    [Positional]
    public required TimeSpan Period
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Period), "The period must be longer than zero.");
    }

    /// <summary>How many ticks to emit before finishing. Without it, Interval runs until stopped.</summary>
    public long? Count
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Count), "The count must be at least 1.");
    }

    /// <summary>
    /// The number of the first tick, such as 0 to match Enumerate's index (Q163). 1 unless set. It doesn't change how
    /// many ticks <see cref="Count"/> emits.
    /// </summary>
    public long CountStart { get; init; } = 1;

    /// <summary>Text to put in each tick, for telling intervals apart.</summary>
    public string? Payload { get; init; }
}

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

/// <summary>Resolves paths written in pipelines.</summary>
internal static class Paths
{
    /// <summary>Turns a leading <c>~</c> into the home folder, and makes the path absolute.</summary>
    public static string Expand(string path) => Path.GetFullPath(Home(path));

    /// <summary>
    /// The full path a computed path names, for a module that reads or writes it (S2). Unless
    /// <paramref name="anyPath"/> is set, what the message gave can't make the path absolute, start it with <c>~</c>,
    /// or climb out of a folder with <c>..</c>, and can't be part of a file:// address. With a
    /// <paramref name="root"/>, relative paths start from it, and the path, once worked out, has to be inside it.
    /// </summary>
    /// <exception cref="PathRefusedException">The path breaks one of those rules.</exception>
    public static string Resolve(IReadOnlyList<TextPiece> pieces, string? root, bool anyPath)
    {
        var text = string.Concat(pieces.Select(piece => piece.Text));
        if (!anyPath)
            CheckPieces(pieces, text);
        var local = Addresses.FilePath(text) ?? text;
        if (root is null)
            return Expand(local);

        var folder = Expand(root);
        string path;
        try
        {
            path = Path.GetFullPath(Home(local), folder);
        }
        catch (ArgumentException exception)
        {
            throw new PathRefusedException($"{text} isn't a path: {exception.Message}");
        }
        var relative = Path.GetRelativePath(folder, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new PathRefusedException($"{path} is outside {folder}, the root= folder.");
        return path;
    }

    // Refuses a path the message steered outside where the pipeline put it.
    private static void CheckPieces(IReadOnlyList<TextPiece> pieces, string text)
    {
        // Which characters of the joined text the message gave.
        var given = new bool[text.Length];
        var at = 0;
        var any = false;
        foreach (var piece in pieces)
        {
            if (piece.FromMessage)
            {
                Array.Fill(given, true, at, piece.Text.Length);
                any |= piece.Text.Length > 0;
            }
            at += piece.Text.Length;
        }
        if (!any)
            return;
        bool Given(int start, int length) => Array.IndexOf(given, true, start, length) >= 0;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile && !Path.IsPathRooted(text))
            throw Refused(text, "the message gave part of a file:// address; write the path without file:// to have it checked");
        if (text.StartsWith('~') && given[0])
            throw Refused(text, "the message started it with ~, which would put it in a home folder");
        if (Path.GetPathRoot(text) is { Length: > 0 } rootPart && Given(0, rootPart.Length))
            throw Refused(text, "the message made it an absolute path");

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '/' && text[i] != Path.DirectorySeparatorChar)
                continue;
            if (i - start == 2 && text[start] == '.' && text[start + 1] == '.' && Given(start, 2))
                throw Refused(text, "the message put '..' in it, which climbs out of the folder");
            start = i + 1;
        }
    }

    private static PathRefusedException Refused(string text, string why) =>
        new($"{text} isn't used, because {why}. Set any-path=true if the message should choose any path.");

    private static string Home(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;
}

/// <summary>A computed path was refused, because the message steered it somewhere the pipeline didn't put it (S2).</summary>
internal sealed class PathRefusedException(string message) : Exception(message);
