namespace Meep2.Security;

/// <summary>
/// Where a host's pipelines get the trust store they verify against: one store, read again only when this process
/// changes it, for tests and embedding, or this machine's, kept current by a <see cref="TrustStoreWatcher"/> started
/// the first time it's asked for.
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

    /// <summary>
    /// The store as it is now, if there is one, without making one: for the gates on Meep's own permissions, which
    /// every pipeline has, and which work without a store (Q279). This machine's is read and watched from the first
    /// time its folder is found, which is looked for at most every ten seconds until then.
    /// </summary>
    public virtual TrustStore? Existing => Current;

    /// <summary>
    /// Whether there's a store that can't be read, as opposed to none: then nothing is held that the store could have
    /// narrowed, Meep's own permissions included, until it can be read (fail closed).
    /// </summary>
    public virtual bool CantBeRead => false;

    /// <summary>
    /// Raised after the store changes, by a watcher's reload or by <see cref="Reload"/>, so a running pipeline can
    /// check again what its sources started with (Q319).
    /// </summary>
    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    protected void OnChanged() => Changed?.Invoke();

    /// <summary>
    /// Reads the store again now, after something in this process changed it, such as <c>Authorise</c> recording a
    /// grant, so what it changed is in force at once rather than once a watcher notices. Returns the store as it is
    /// then, or null if there's none. A store that can't be read again stays as it was.
    /// </summary>
    public virtual TrustStore? Reload() => Current;

    /// <summary>This store, read again only when <see cref="Reload"/> is called.</summary>
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
        private readonly Lock _reloading = new();
        private TrustStore _store = store;

        public override TrustStore Current => Volatile.Read(ref _store);

        public override TrustStore Reload()
        {
            lock (_reloading)
            {
                try
                {
                    var before = _store;
                    Volatile.Write(ref _store, _store.Reload());
                    if (!ReferenceEquals(before, _store))
                        OnChanged();
                }
                catch (TrustStoreException)
                {
                    // It stays as it was, as a watcher's does.
                }
                return _store;
            }
        }
    }

    private sealed class Watched(TrustFolder folder, Action<string> log) : TrustStoreSource
    {
        // How long a store that couldn't be read is left before trying again.
        private static readonly TimeSpan s_retry = TimeSpan.FromSeconds(10);

        private readonly Lock _starting = new();
        private TrustStoreWatcher? _watcher;
        private long _failedAt;
        private long _missingAt;
        private bool _disposed;

        public override TrustStore? Existing
        {
            get
            {
                if (Volatile.Read(ref _watcher) is { } running)
                    return running.Current;
                var missingAt = Volatile.Read(ref _missingAt);
                if (missingAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(missingAt) < s_retry)
                    return null;
                if (!Directory.Exists(folder.Path))
                {
                    Volatile.Write(ref _missingAt, System.Diagnostics.Stopwatch.GetTimestamp());
                    return null;
                }
                return Current;
            }
        }

        public override bool CantBeRead => Volatile.Read(ref _watcher) is null && Volatile.Read(ref _failedAt) != 0;

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
                var watcher = new TrustStoreWatcher(folder, log);
                watcher.Reloaded += _ => OnChanged();
                Volatile.Write(ref _watcher, watcher);
                foreach (var problem in _watcher.Current.Problems)
                    log($"[trust] {problem}");
            }
            catch (Exception exception) when (exception is TrustStoreException or IOException or UnauthorizedAccessException)
            {
                _failedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                log($"[trust] the trust store in {folder.Path} can't be read, so nothing verifies until it can: {exception.Message}");
            }
        }

        public override TrustStore? Reload() => Volatile.Read(ref _watcher) is { } running ? running.Reload() : Current;

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
