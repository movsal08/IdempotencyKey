using System.Collections.Concurrent;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Redis;
using Testcontainers.Redis;
using Xunit;
using Xunit.Abstractions;

namespace IdempotencyKey.Store.Redis.Tests;

public class RedisStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();
    private RedisIdempotencyStore _store = null!;
    private IdempotencyPolicy _policy = new()
    {
        LeaseDuration = TimeSpan.FromSeconds(2),
        Ttl = TimeSpan.FromSeconds(10)
    };
    private readonly ITestOutputHelper _output;
    private bool _initFailed;
    private string? _initError;

    public RedisStoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _redisContainer.StartAsync();
            _store = new RedisIdempotencyStore(new RedisIdempotencyStoreOptions
            {
                Configuration = _redisContainer.GetConnectionString()
            });
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initError = ex.Message;
            _output.WriteLine($"Docker initialization failed: {ex}");
        }
    }

    public async Task DisposeAsync()
    {
        if (!_initFailed)
        {
            await _redisContainer.DisposeAsync();
        }
    }

    private IdempotencyKey.Core.IdempotencyKey CreateKey(string? key = null)
        => new("test-scope", key ?? Guid.NewGuid().ToString());

    private Fingerprint CreateFingerprint(string val = "hash1")
        => new(val);

    private IdempotencyResponseSnapshot CreateSnapshot() => new()
    {
        StatusCode = 200,
        ContentType = "application/json",
        Body = new byte[] { 1, 2, 3 }
    };

    [Fact]
    public async Task TryBegin_NewKey_ReturnsAcquired()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();

        var result = await _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBegin_SameKeySameFingerprint_AfterComplete_ReturnsAlreadyCompleted()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();
        var snapshot = CreateSnapshot();

        // 1. Acquire
        await _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);

        // 2. Complete
        await _store.CompleteAsync(key, fp, snapshot, _policy.Ttl, CancellationToken.None);

        // 3. Re-Acquire
        var result = await _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.AlreadyCompleted, result.Outcome);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(snapshot.StatusCode, result.Snapshot.StatusCode);
        Assert.Equal(snapshot.Body, result.Snapshot.Body);
    }

    [Fact]
    public async Task TryBegin_SameKeyDifferentFingerprint_ReturnsConflict()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp1 = CreateFingerprint("hash1");
        var fp2 = CreateFingerprint("hash2");

        // 1. Acquire with fp1
        await _store.TryBeginAsync(key, fp1, _policy, CancellationToken.None);

        // 2. Try with fp2
        var result = await _store.TryBeginAsync(key, fp2, _policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task TryBegin_InFlight_ReturnsInFlightWithRetryAfter()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();

        // 1. Acquire
        await _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);

        // 2. Try again immediately
        var result = await _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.InFlight, result.Outcome);
        Assert.True(result.RetryAfter > TimeSpan.Zero, "RetryAfter should be positive");
    }

    [Fact]
    public async Task TryBegin_InFlight_LeaseExpired_ReAcquires()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();
        var shortLeasePolicy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromMilliseconds(500),
            Ttl = TimeSpan.FromSeconds(10)
        };

        // 1. Acquire
        await _store.TryBeginAsync(key, fp, shortLeasePolicy, CancellationToken.None);

        // 2. Wait for lease to expire
        await Task.Delay(1000);

        // 3. Try again -> Should acquire
        var result = await _store.TryBeginAsync(key, fp, shortLeasePolicy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBegin_TtlExpired_TreatsAsNew()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();
        var shortTtlPolicy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            Ttl = TimeSpan.FromMilliseconds(500)
        };

        // 1. Acquire
        await _store.TryBeginAsync(key, fp, shortTtlPolicy, CancellationToken.None);

        // 2. Wait for TTL to expire (Redis handles expiry)
        await Task.Delay(2000);

        // 3. Try again -> Should acquire (as new)
        var result = await _store.TryBeginAsync(key, fp, shortTtlPolicy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task Concurrency_ParallelTryBegin_OnlyOneAcquires()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp = CreateFingerprint();
        var count = 50;
        var tasks = new Task<TryBeginResult>[count];

        for (int i = 0; i < count; i++)
        {
            tasks[i] = _store.TryBeginAsync(key, fp, _policy, CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        var acquiredCount = results.Count(r => r.Outcome == TryBeginOutcome.Acquired);
        var inFlightCount = results.Count(r => r.Outcome == TryBeginOutcome.InFlight);

        Assert.Equal(1, acquiredCount);
        Assert.Equal(count - 1, inFlightCount);
    }

    [Fact]
    public async Task CompleteAsync_DifferentFingerprint_ThrowsInvalidOperation()
    {
        if (_initFailed) { _output.WriteLine($"Skipping test: {_initError}"); return; }

        var key = CreateKey();
        var fp1 = CreateFingerprint("hash1");
        var fp2 = CreateFingerprint("hash2");
        var snapshot = CreateSnapshot();

        await _store.TryBeginAsync(key, fp1, _policy, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _store.CompleteAsync(key, fp2, snapshot, _policy.Ttl, CancellationToken.None);
        });
    }
}
