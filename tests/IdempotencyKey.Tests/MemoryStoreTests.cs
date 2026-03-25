using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;

// Alias to avoid conflict with root namespace
using IdempotencyKeyType = IdempotencyKey.Core.IdempotencyKey;

namespace IdempotencyKey.Tests;

public class MemoryStoreTests : IDisposable
{
    private readonly MemoryIdempotencyStore _store;
    private readonly IdempotencyKeyType _key;
    private readonly Fingerprint _fingerprint;
    private readonly IdempotencyPolicy _policy;

    public MemoryStoreTests()
    {
        _store = new MemoryIdempotencyStore();
        _key = new IdempotencyKeyType("test-scope", Guid.NewGuid().ToString());
        _fingerprint = new Fingerprint("hash123");
        _policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            Ttl = TimeSpan.FromHours(1)
        };
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public async Task TryBegin_FirstCall_ReturnsAcquired()
    {
        var result = await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBegin_SecondCallSameFingerprint_ReturnsInFlight()
    {
        await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);

        var result = await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.InFlight, result.Outcome);
        Assert.NotNull(result.RetryAfter);
    }

    [Fact]
    public async Task TryBegin_SecondCallDiffFingerprint_ReturnsConflict()
    {
        await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);

        var diffFingerprint = new Fingerprint("hash456");
        var result = await _store.TryBeginAsync(_key, diffFingerprint, _policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task CompleteAsync_MakesItAlreadyCompleted_AndSetsMetadata()
    {
        await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);

        var snapshot = new IdempotencyResponseSnapshot { StatusCode = 200, Body = [1, 2, 3] };
        await _store.CompleteAsync(_key, _fingerprint, snapshot, _policy.Ttl, CancellationToken.None);

        var result = await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.AlreadyCompleted, result.Outcome);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(200, result.Snapshot.StatusCode);

        // Assert metadata was set
        Assert.NotEqual(default, result.Snapshot.CreatedAtUtc);
        Assert.NotEqual(default, result.Snapshot.ExpiresAtUtc);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotMutateInputSnapshot()
    {
        await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);

        var snapshot = new IdempotencyResponseSnapshot
        {
            StatusCode = 200,
            CreatedAtUtc = default,
            ExpiresAtUtc = default
        };

        await _store.CompleteAsync(_key, _fingerprint, snapshot, _policy.Ttl, CancellationToken.None);

        Assert.Equal(default, snapshot.CreatedAtUtc);
        Assert.Equal(default, snapshot.ExpiresAtUtc);
    }

    [Fact]
    public async Task CompleteAsync_WhenEntryMissing_Throws()
    {
        var snapshot = new IdempotencyResponseSnapshot { StatusCode = 200 };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _store.CompleteAsync(_key, _fingerprint, snapshot, _policy.Ttl, CancellationToken.None);
        });
    }

    [Fact]
    public async Task TryBegin_AfterLeaseExpires_ReturnsAcquired()
    {
        var shortPolicy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromMilliseconds(100),
            Ttl = TimeSpan.FromHours(1)
        };

        await _store.TryBeginAsync(_key, _fingerprint, shortPolicy, CancellationToken.None);

        // Wait for lease to expire
        await Task.Delay(200);

        var result = await _store.TryBeginAsync(_key, _fingerprint, shortPolicy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBegin_AfterTtlExpires_ReturnsAcquired_New()
    {
        var shortPolicy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            Ttl = TimeSpan.FromMilliseconds(100)
        };

        await _store.TryBeginAsync(_key, _fingerprint, shortPolicy, CancellationToken.None);

        // Complete it
        var snapshot = new IdempotencyResponseSnapshot { StatusCode = 200 };
        // Pass short TTL to CompleteAsync
        await _store.CompleteAsync(_key, _fingerprint, snapshot, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        // Verify it is completed
        var result1 = await _store.TryBeginAsync(_key, _fingerprint, shortPolicy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.AlreadyCompleted, result1.Outcome);

        // Wait for TTL
        await Task.Delay(200);

        // Should be missing/new now
        var result2 = await _store.TryBeginAsync(_key, _fingerprint, shortPolicy, CancellationToken.None);
        Assert.Equal(TryBeginOutcome.Acquired, result2.Outcome);
    }

    [Fact]
    public async Task Concurrency_ParallelTryBegin_OnlyOneAcquired()
    {
        var tasks = new List<Task<TryBeginResult>>();
        int count = 50;

        using (var barrier = new Barrier(count))
        {
            for (int i = 0; i < count; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await _store.TryBeginAsync(_key, _fingerprint, _policy, CancellationToken.None);
                }));
            }

            var results = await Task.WhenAll(tasks);

            var acquired = results.Count(r => r.Outcome == TryBeginOutcome.Acquired);
            var inFlight = results.Count(r => r.Outcome == TryBeginOutcome.InFlight);

            Assert.Equal(1, acquired);
            Assert.Equal(count - 1, inFlight);
        }
    }
}
