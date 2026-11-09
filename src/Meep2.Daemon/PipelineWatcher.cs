using Meep2.Meeplang.Editing;

namespace Meep2.Daemon;

/// <summary>
/// Reloads a running pipeline whenever its file changes (goal 4), for <c>meep run --watch</c> and
/// <c>meep start --watch</c>. Editors save in bursts, and some by renaming a new file into place, so it waits for the
/// file to settle first.
/// </summary>
public sealed class PipelineWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _settle;
    private readonly Func<CancellationToken, Task<LiveChange>> _reload;
    private readonly Action<string> _log;
    private readonly string _name;
    private int _disposed;

    /// <summary>Starts watching.</summary>
    /// <param name="path">The pipeline's file.</param>
    /// <param name="name">The pipeline's name, for what's logged.</param>
    /// <param name="reload">Reloads the pipeline from its file.</param>
    /// <param name="log">Told what each reload did, or why it couldn't.</param>
    public PipelineWatcher(string path, string name, Func<CancellationToken, Task<LiveChange>> reload, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(path);
        _reload = reload;
        _log = log;
        _name = name;
        path = Path.GetFullPath(path);
        _settle = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (sender, e) => Changed(sender, e);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>How long the file must be left alone before it's reloaded.</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _watcher.Dispose();
        _settle.Dispose();
    }

    private void Changed(object sender, FileSystemEventArgs e)
    {
        try
        {
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Stopped watching meanwhile.
        }
    }

    private async Task ReloadAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            var change = await _reload(CancellationToken.None).ConfigureAwait(false);
            if (!change.Applied)
            {
                _log($"[{_name}] its file changed, but doesn't load, so it runs as it was:");
                foreach (var diagnostic in change.Diagnostics.Where(d => d.IsError))
                    _log($"  {diagnostic}");
            }
            else if (change.Report!.ChangedAnything)
            {
                _log($"[{_name}] reloaded: {HostedPipeline.Summary(change.Report)}");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"[{_name}] couldn't be reloaded: {exception.Message}");
        }
    }
}
