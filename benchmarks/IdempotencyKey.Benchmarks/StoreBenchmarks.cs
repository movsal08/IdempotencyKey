using BenchmarkDotNet.Attributes;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using System.Threading;
using System.Threading.Tasks;

namespace Benchmarks;

[MemoryDiagnoser]
public class StoreBenchmarks
{
    private MemoryIdempotencyStore _store = null!;
    private Fingerprint _fingerprint;
    private IdempotencyPolicy _policy = null!;

    // For contended test
    private const int Parallelism = 4;
    private IdempotencyKey.Core.IdempotencyKey _contendedKey;
    // For uncontended test
    private IdempotencyKey.Core.IdempotencyKey _uncontendedKey;

    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryIdempotencyStore();
        // Fingerprint is a record struct with a string Value
        // Check Fingerprint.cs constructor
        _fingerprint = new Fingerprint("dummy-fingerprint");
        _policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            Ttl = TimeSpan.FromMinutes(5)
        };

        _contendedKey = new IdempotencyKey.Core.IdempotencyKey("scope", "contended-key");
        _uncontendedKey = new IdempotencyKey.Core.IdempotencyKey("scope", "uncontended-key");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
    }

    [Benchmark]
    public async Task TryBegin_Uncontended()
    {
        await _store.TryBeginAsync(_uncontendedKey, _fingerprint, _policy, CancellationToken.None);
    }

    [Benchmark(OperationsPerInvoke = Parallelism)]
    public async Task TryBegin_Contended()
    {
        var tasks = new Task[Parallelism];
        for (int i = 0; i < Parallelism; i++)
        {
            tasks[i] = _store.TryBeginAsync(_contendedKey, _fingerprint, _policy, CancellationToken.None);
        }

        await Task.WhenAll(tasks);
    }
}
