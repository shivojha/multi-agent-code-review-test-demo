#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace InMemoryCaching;

/// <summary>
/// Represents a generic, thread-safe in-memory cache with per-entry time-to-live expiration.
/// </summary>
/// <typeparam name="TValue">The type of values stored in the cache.</typeparam>
public sealed class InMemoryCache<TValue> : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval;
    private readonly object _disposeLock = new();
    private int _disposed; // FIXED: use an interlocked state flag to reduce race windows during disposal checks.

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryCache{TValue}"/> class.
    /// </summary>
    /// <param name="cleanupInterval">
    /// The interval at which the cache performs background cleanup of expired entries.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="cleanupInterval"/> is less than or equal to zero.
    /// </exception>
    public InMemoryCache(TimeSpan cleanupInterval)
    {
        if (cleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupInterval), "Cleanup interval must be greater than zero.");
        }

        _cleanupInterval = cleanupInterval;
        _entries = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        _cleanupTimer = new Timer(
            static state => ((InMemoryCache<TValue>)state!).CleanupExpiredEntries(),
            this,
            _cleanupInterval,
            _cleanupInterval);
    }

    /// <summary>
    /// Gets the current number of entries in the cache, including entries that may have expired
    /// but have not yet been cleaned up by the background timer.
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return _entries.Count;
        }
    }

    /// <summary>
    /// Gets a cached value by key, or creates and stores it if it does not exist or has expired.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">The factory used to create the value when needed.</param>
    /// <param name="ttl">The time-to-live for the created or refreshed entry.</param>
    /// <returns>The existing or newly created cached value.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ttl"/> is less than or equal to zero.</exception>
    public TValue GetOrCreate(string key, Func<TValue> factory, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace.", nameof(key));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be greater than zero.");
        }

        ThrowIfDisposed();

        while (true)
        {
            ThrowIfDisposed();

            var now = DateTimeOffset.UtcNow;

            // FIXED: Store a per-key Lazy<TValue> so factory execution is single-publication under contention.
            // FIXED: This prevents multiple concurrent threads from executing the factory for the same key.
            var lazyEntry = _entries.GetOrAdd(
                key,
                static (k, state) =>
                {
                    var (factoryLocal, ttlLocal) = ((Func<TValue> Factory, TimeSpan Ttl))state!;
                    return CacheEntry.CreatePending(factoryLocal, ttlLocal);
                },
                (factory, ttl));

            if (lazyEntry.IsExpired(now))
            {
                // FIXED: Remove expired entries using a compare-and-remove loop and retry to avoid stale race windows.
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, lazyEntry));
                continue;
            }

            try
            {
                var value = lazyEntry.GetValue();
                ThrowIfDisposed();
                return value;
            }
            catch
            {
                // FIXED: If value creation fails, remove the failed placeholder so future calls can retry.
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, lazyEntry));
                throw;
            }
        }
    }

    /// <summary>
    /// Removes the cache entry associated with the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>
    /// <see langword="true"/> if the entry was removed successfully; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null or whitespace.</exception>
    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace.", nameof(key));
        }

        ThrowIfDisposed();
        return _entries.TryRemove(key, out _);
    }

    /// <summary>
    /// Removes all cache entries.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _entries.Clear();
    }

    /// <summary>
    /// Releases all resources used by the cache and stops the background cleanup timer.
    /// </summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
        }

        _cleanupTimer.Dispose();
        _entries.Clear();
        GC.SuppressFinalize(this);
    }

    private void CleanupExpiredEntries()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _entries)
        {
            if (kvp.Value.IsExpired(now))
            {
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(kvp.Key, kvp.Value));
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(InMemoryCache<TValue>));
        }
    }

    private sealed class CacheEntry
    {
        private readonly Lazy<TValue>? _lazyValue;
        private readonly DateTimeOffset _expiresAt;
        private readonly bool _isPending;

        private CacheEntry(Lazy<TValue> lazyValue, DateTimeOffset expiresAt)
        {
            _lazyValue = lazyValue;
            _expiresAt = expiresAt;
            _isPending = true;
        }

        private CacheEntry(TValue value, DateTimeOffset expiresAt)
        {
            Value = value;
            _expiresAt = expiresAt;
            _isPending = false;
        }

        public TValue Value { get; }

        public static CacheEntry CreatePending(Func<TValue> factory, TimeSpan ttl)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
            var lazy = new Lazy<TValue>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
            return new CacheEntry(lazy, expiresAt);
        }

        public TValue GetValue()
        {
            return _isPending
                ? _lazyValue!.Value
                : Value;
        }

        public bool IsExpired(DateTimeOffset utcNow) => utcNow >= _expiresAt;
    }
}