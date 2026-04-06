#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InMemoryCaching;
using Xunit;

namespace InMemoryCaching.Tests;

public class InMemoryCacheTests : IDisposable
{
    private readonly InMemoryCache<string> _cache;

    public InMemoryCacheTests()
    {
        _cache = new InMemoryCache<string>(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void InMemoryCache_Constructor_ValidInterval_CreatesInstance()
    {
        using var cache = new InMemoryCache<int>(TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InMemoryCache_Constructor_InvalidCleanupInterval_ThrowsArgumentOutOfRangeException(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryCache<string>(TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("cleanupInterval", ex.ParamName);
    }

    [Fact]
    public void GetOrCreate_NewKey_CreatesAndReturnsValue()
    {
        var value = _cache.GetOrCreate("key1", () => "value1", TimeSpan.FromSeconds(1));

        Assert.Equal("value1", value);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public void GetOrCreate_ExistingUnexpiredKey_ReturnsExistingValueWithoutInvokingFactory()
    {
        _cache.GetOrCreate("key1", () => "value1", TimeSpan.FromSeconds(1));

        var factoryCalls = 0;
        var value = _cache.GetOrCreate("key1", () =>
        {
            factoryCalls++;
            return "value2";
        }, TimeSpan.FromSeconds(1));

        Assert.Equal("value1", value);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public void GetOrCreate_ExpiredKey_ReplacesValue()
    {
        using var cache = new InMemoryCache<string>(TimeSpan.FromHours(1));

        cache.GetOrCreate("key1", () => "value1", TimeSpan.FromMilliseconds(20));
        Thread.Sleep(50);

        var value = cache.GetOrCreate("key1", () => "value2", TimeSpan.FromSeconds(1));

        Assert.Equal("value2", value);
    }

    [Fact]
    public void GetOrCreate_NullKey_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => _cache.GetOrCreate(null!, () => "value", TimeSpan.FromSeconds(1)));

        Assert.Equal("key", ex.ParamName);
    }

    [Theory]
    [InlineData("")]

// empty string
    [InlineData("   ")]
    public void GetOrCreate_WhitespaceKey_ThrowsArgumentException(string key)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => _cache.GetOrCreate(key, () => "value", TimeSpan.FromSeconds(1)));

        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void GetOrCreate_NullFactory_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => _cache.GetOrCreate("key1", null!, TimeSpan.FromSeconds(1)));

        Assert.Equal("factory", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetOrCreate_InvalidTtl_ThrowsArgumentOutOfRangeException(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _cache.GetOrCreate("key1", () => "value", TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("ttl", ex.ParamName);
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        _cache.GetOrCreate("key1", () => "value1", TimeSpan.FromSeconds(1));

        var removed = _cache.Remove("key1");

        Assert.True(removed);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Remove_MissingKey_ReturnsFalse()
    {
        var removed = _cache.Remove("missing");

        Assert.False(removed);
    }

    [Fact]
    public void Remove_NullKey_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => _cache.Remove(null!));

        Assert.Equal("key", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Remove_WhitespaceKey_ThrowsArgumentException(string key)
    {
        var ex = Assert.Throws<ArgumentException>(() => _cache.Remove(key));

        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _cache.GetOrCreate("a", () => "1", TimeSpan.FromSeconds(1));
        _cache.GetOrCreate("b", () => "2", TimeSpan.FromSeconds(1));

        _cache.Clear();

        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Dispose_AfterDispose_GetOrCreateThrowsObjectDisposedException()
    {
        _cache.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => _cache.GetOrCreate("key1", () => "value", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Dispose_AfterDispose_RemoveThrowsObjectDisposedException()
    {
        _cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _cache.Remove("key1"));
    }

    [Fact]
    public void Dispose_AfterDispose_ClearThrowsObjectDisposedException()
    {
        _cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _cache.Clear());
    }

    [Fact]
    public void Dispose_Idempotent_CanBeCalledMultipleTimes()
    {
        _cache.Dispose();
        _cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _cache.Count);
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentCallsFactoryInvokedOnce_ReturnsSameValue()
    {
        using var cache = new InMemoryCache<int>(TimeSpan.FromSeconds(1));

        var factoryCalls = 0;
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        int Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            started.Set();
            release.Wait();
            return 42;
        }

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => cache.GetOrCreate("k", Factory, TimeSpan.FromSeconds(1))))
            .ToArray();

        started.Wait();
        release.Set();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public void GetOrCreate_DisposedWhileFactoryRunning_DoesNotPublishValue()
    {
        using var cache = new InMemoryCache<string>(TimeSpan.FromSeconds(1));

        var factoryStarted = new ManualResetEventSlim(false);
        var continueFactory = new ManualResetEventSlim(false);

        var task = Task.Run(() =>
            cache.GetOrCreate("k", () =>
            {
                factoryStarted.Set();
                continueFactory.Wait();
                return "value";
            }, TimeSpan.FromSeconds(1)));

        factoryStarted.Wait();
        cache.Dispose();
        continueFactory.Set();

        var ex = Assert.ThrowsAny<Exception>(() => task.GetAwaiter().GetResult());
        Assert.True(ex is ObjectDisposedException || ex is InvalidOperationException);
    }

    [Fact]
    public void Count_ExpiredEntriesMayRemainUntilCleanupOrAccess_RemainsConsistentWithDocumentation()
    {
        using var cache = new InMemoryCache<string>(TimeSpan.FromHours(1));

        cache.GetOrCreate("key1", () => "value1", TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, cache.Count);

        Thread.Sleep(50);

        Assert.Equal(1, cache.Count);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}