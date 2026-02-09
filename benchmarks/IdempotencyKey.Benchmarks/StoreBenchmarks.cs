using BenchmarkDotNet.Attributes;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using IdempotencyKeyType = IdempotencyKey.Core.IdempotencyKey;

namespace Benchmarks;

[MemoryDiagnoser]
public class StoreBenchmarks
{
    private MemoryIdempotencyStore _memoryStore = null!;
    private IdempotencyPolicy _policy = null!;
    private Fingerprint _fingerprint;
    private IdempotencyResponseSnapshot _snapshot = null!;
    private long _counter;
    private IdempotencyKeyType _replayKey;

    [GlobalSetup]
    public void Setup()
    {
        _memoryStore = new MemoryIdempotencyStore();
        _policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            Ttl = TimeSpan.FromHours(1)
        };
        _fingerprint = new Fingerprint("hash-benchmark");
        _snapshot = new IdempotencyResponseSnapshot
        {
            StatusCode = 200,
            Body = new byte[100] // simple body
        };
        _counter = 0;

        // Prepare a key that is already completed for the Replay benchmark
        _replayKey = new IdempotencyKeyType("bench", "replay-static");
        _memoryStore.TryBeginAsync(_replayKey, _fingerprint, _policy, CancellationToken.None).GetAwaiter().GetResult();
        _memoryStore.CompleteAsync(_replayKey, _fingerprint, _snapshot, _policy.Ttl, CancellationToken.None).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _memoryStore.Dispose();
    }

    [Benchmark]
    public async Task Acquire_New()
    {
        var id = Interlocked.Increment(ref _counter);
        var key = new IdempotencyKeyType("bench", id.ToString());
        await _memoryStore.TryBeginAsync(key, _fingerprint, _policy, CancellationToken.None);
    }

    [Benchmark]
    public async Task Acquire_And_Complete()
    {
        var id = Interlocked.Increment(ref _counter);
        // Use a different prefix/range to avoid collision with Acquire_New if run in same process (though unlikely to collide due to unique ID)
        var key = new IdempotencyKeyType("bench", "c-" + id.ToString());

        await _memoryStore.TryBeginAsync(key, _fingerprint, _policy, CancellationToken.None);
        await _memoryStore.CompleteAsync(key, _fingerprint, _snapshot, _policy.Ttl, CancellationToken.None);
    }

    [Benchmark]
    public async Task Replay_Existing()
    {
        // Always query the same key which is already completed
        await _memoryStore.TryBeginAsync(_replayKey, _fingerprint, _policy, CancellationToken.None);
    }
}
