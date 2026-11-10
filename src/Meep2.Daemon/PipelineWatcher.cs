using Meep2.Meeplang.Editing;

namespace Meep2.Daemon;

/// <summary>
/// Reloads a running pipeline whenever its file, or a file it includes, changes (goal 4), for <c>meep run --watch</c>
/// and <c>meep start --watch</c>. Editors save in bursts, and some by renaming a new file into place, so it waits for
/// the files to settle first.
/// </summary>
public sealed class PipelineWatcher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileSystemWatcher> _folders = new(StringComparer.Ordinal);
    private readonly Timer _settle;
    private readonly Func<CancellationToken, Task<LiveChange>> _reload;
    private readonly Func<IEnumerable<string>> _files;
    private readonly Action<string> _log;
    private readonly string _name;
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Starts watching.</summary>
    /// <param name="path">The pipeline's file.</param>
    /// <param name="name">The pipeline's name, for what's logged.</param>
    /// <param name="reload">Reloads the pipeline from its file.</param>
    /// <param name="log">Told what each reload did, or why it couldn't.</param>
    /// <param name="includes">The files the pipeline includes now, asked again after each reload, since they can change.</param>
    public PipelineWatcher(string path, string name, Func<CancellationToken, Task<LiveChange>> reload, Action<string> log, Func<IEnumerable<string>>? includes = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        _reload = reload;
        _log = log;
        _name = name;
        var main = Path.GetFullPath(path);
        _files = () => includes is null ? [main] : [main, .. includes().Select(Path.GetFullPath)];
        _settle = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
        Refresh();
    }

    /// <summary>How long the files must be left alone before the pipeline is reloaded.</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The files being watched now.</summary>
    public IReadOnlyCollection<string> Files
    {
        get
        {
            lock (_lock)
                return [.. _watched];
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_lock)
        {
            foreach (var watcher in _folders.Values)
                watcher.Dispose();
            _folders.Clear();
        }
        _settle.Dispose();
    }

    // Watches the folders the files are in, for the files that matter.
    private void Refresh()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _watched = new HashSet<string>(_files(), StringComparer.Ordinal);
            foreach (var folder in _watched.Select(f => Path.GetDirectoryName(f)!).Distinct())
            {
                if (_folders.ContainsKey(folder) || !Directory.Exists(folder))
                    continue;
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Renamed += (sender, e) => Changed(sender, e);
                watcher.EnableRaisingEvents = true;
                _folders[folder] = watcher;
            }
        }
    }

    private void Changed(object sender, FileSystemEventArgs e)
    {
        bool matters;
        lock (_lock)
            matters = _watched.Contains(e.FullPath) || (e is RenamedEventArgs renamed && _watched.Contains(renamed.OldFullPath));
        if (!matters)
            return;
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
            else if (change.Report!.ChangedAnything || change.SettingsChanged)
            {
                _log($"[{_name}] reloaded: {HostedPipeline.Summary(change.Report)}");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"[{_name}] couldn't be reloaded: {exception.Message}");
        }
        Refresh(); // it may include other files now
    }
}
