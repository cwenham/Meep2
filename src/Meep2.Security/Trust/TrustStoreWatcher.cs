using Meep2.Shared;

namespace Meep2.Security;

/// <summary>
/// Keeps a <see cref="TrustStore"/> current: loads it once, then reloads it whenever a file in its folder changes, as
/// <c>PipelineWatcher</c> does for a pipeline, waiting for the files to settle first. A reload that fails is logged,
/// and the store as it was stays in force.
/// </summary>
public sealed class TrustStoreWatcher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _settle;
    private readonly Action<string> _log;
    private TrustStore _current;
    private int _disposed;

    /// <summary>
    /// Loads the store in <paramref name="folder"/>, making the folder, kept to the user, if it isn't there, and starts
    /// watching it. Throws <see cref="TrustStoreException"/> if the store can't be loaded at all.
    /// </summary>
    /// <param name="folder">The trust store's folder.</param>
    /// <param name="log">Told what each reload changed, or why it couldn't.</param>
    public TrustStoreWatcher(TrustFolder folder, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        PrivateFolders.Create(folder.Path);
        _current = TrustStore.Load(folder);
        _settle = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.CreationTime | NotifyFilters.Attributes,
        };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += (sender, e) => Changed(sender, e);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>How long the files must be left alone before the store is reloaded.</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The store as it is now.</summary>
    public TrustStore Current => Volatile.Read(ref _current);

    /// <summary>Raised with the new store after a reload that changed something.</summary>
    public event Action<TrustStore>? Reloaded;

    /// <summary>
    /// Reads the folder again now, rather than waiting for it to settle, and returns the store, which is the same one
    /// if nothing changed or the reload failed.
    /// </summary>
    public TrustStore Reload()
    {
        TrustStore? changed = null;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return _current;
            try
            {
                var next = _current.Reload();
                if (!ReferenceEquals(next, _current))
                {
                    var revoked = next.RevocationVersion != _current.RevocationVersion;
                    Volatile.Write(ref _current, next);
                    _log($"[trust] reloaded: version {next.Version}{(revoked ? ", taking something away, so verifications are checked again" : "")}.");
                    foreach (var problem in next.Problems)
                        _log($"[trust]   {problem}");
                    changed = next;
                }
            }
            catch (TrustStoreException exception)
            {
                _log($"[trust] its files changed, but can't be read, so it stays as it was: {exception.Message}");
            }
        }
        if (changed is not null)
            Reloaded?.Invoke(changed);
        return Current;
    }

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
        // The lock writers hold, and the files they write before renaming them into place, aren't the store.
        if (e.Name is { } name && (Path.GetFileName(name) == ".lock" || name.EndsWith(".new", StringComparison.Ordinal)))
            return;
        // A file being written in place, then renamed over the old one, settles as one change.
        try
        {
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Stopped watching meanwhile.
        }
    }
}
