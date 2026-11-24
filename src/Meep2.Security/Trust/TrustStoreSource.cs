namespace Meep2.Security;

/// <summary>
/// Where a host's pipelines get the trust store they verify against: one store that never changes, for tests and
/// embedding, or this machine's, kept current by a <see cref="TrustStoreWatcher"/> started the first time it's asked for.
/// </summary>
/// <remarks>
/// The daemon asks for the store when it starts, so a store that can't be read is reported then. <c>meep run</c> only
/// asks when a pipeline first verifies something, so pipelines that don't verify neither make the folder nor watch it.
/// </remarks>
public abstract class TrustStoreSource : IDisposable
{
    /// <summary>
    /// The store as it is now, or null if it can't be read, in which case nothing verifies (and the reason was logged).
    /// </summary>
    public abstract TrustStore? Current { get; }

    /// <summary>Always the same store.</summary>
    public static TrustStoreSource Of(TrustStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new Fixed(store);
    }

    /// <summary>
    /// The store in <paramref name="folder"/>, loaded and watched from the first time <see cref="Current"/> is read.
    /// </summary>
    /// <param name="folder">The trust store's folder.</param>
    /// <param name="log">Told what each reload changed, and why the store can't be read, if it can't.</param>
    public static TrustStoreSource Watching(TrustFolder folder, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(log);
        return new Watched(folder, log);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops watching, if it was.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    private sealed class Fixed(TrustStore store) : TrustStoreSource
    {
        public override TrustStore Current => store;
    }

    private sealed class Watched(TrustFolder folder, Action<string> log) : TrustStoreSource
    {
        // How long a store that couldn't be read is left before trying again.
        private static readonly TimeSpan s_retry = TimeSpan.FromSeconds(10);

        private readonly Lock _starting = new();
        private TrustStoreWatcher? _watcher;
        private long _failedAt;
        private bool _disposed;

        public override TrustStore? Current
        {
            get
            {
                if (Volatile.Read(ref _watcher) is { } running)
                    return running.Current;
                lock (_starting)
                {
                    if (_watcher is null && !_disposed && (_failedAt == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_failedAt) >= s_retry))
                        Start();
                    return _watcher?.Current;
                }
            }
        }

        // A store that can't be read is tried again when it's next wanted after a while, so a mistake in it is
        // reported every ten seconds at most rather than with every message, and fixing it needs no restart.
        private void Start()
        {
            try
            {
                Volatile.Write(ref _watcher, new TrustStoreWatcher(folder, log));
                foreach (var problem in _watcher.Current.Problems)
                    log($"[trust] {problem}");
            }
            catch (Exception exception) when (exception is TrustStoreException or IOException or UnauthorizedAccessException)
            {
                _failedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                log($"[trust] the trust store in {folder.Path} can't be read, so nothing verifies until it can: {exception.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_starting)
            {
                _disposed = true;
                _watcher?.Dispose();
            }
        }
    }
}
