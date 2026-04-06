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
    private bool _disposed;

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
            var now = DateTimeOffset.UtcNow;

            if (_entries.TryGetValue(key, out var existing))
            {
                if (!existing.IsExpired(now))
                {
                    return existing.Value;
                }

                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, existing));
            }

            var value = factory();
            var newEntry = new CacheEntry(value, now.Add(ttl));

            if (_entries.TryAdd(key, newEntry))
            {
                return value;
            }

            // Another thread may have won the race. Loop to read the authoritative value.
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cleanupTimer.Dispose();
        _entries.Clear();
        GC.SuppressFinalize(this);
    }

    private void CleanupExpiredEntries()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _entries)
        {
            if (kvp.Value.IsExpired(now))
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InMemoryCache<TValue>));
        }
    }

    private sealed record CacheEntry(TValue Value, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset utcNow) => utcNow >= ExpiresAt;
    }
}