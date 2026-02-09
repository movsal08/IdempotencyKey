using BenchmarkDotNet.Attributes;
using IdempotencyKey.Core;

namespace Benchmarks;

[MemoryDiagnoser]
public class FingerprintBenchmarks
{
    private DefaultRequestBodyHasher _hasher = null!;
    private byte[] _emptyBody = null!;
    private byte[] _body1Kb = null!;
    private byte[] _body16Kb = null!;
    private byte[] _body256Kb = null!;

    private Sha256FingerprintProvider _fingerprintProvider = null!;
    private byte[] _bodyHash = null!;
    private Dictionary<string, string[]> _headers = null!;

    [GlobalSetup]
    public void Setup()
    {
        _hasher = new DefaultRequestBodyHasher();
        _emptyBody = Array.Empty<byte>();

        _body1Kb = new byte[1024];
        new Random(42).NextBytes(_body1Kb);

        _body16Kb = new byte[16 * 1024];
        new Random(42).NextBytes(_body16Kb);

        _body256Kb = new byte[256 * 1024];
        new Random(42).NextBytes(_body256Kb);

        _fingerprintProvider = new Sha256FingerprintProvider();
        _bodyHash = _hasher.Hash(_body1Kb);
        _headers = new Dictionary<string, string[]>
        {
            { "Content-Type", new[] { "application/json" } },
            { "User-Agent", new[] { "BenchmarkDotNet" } }
        };
    }

    [Benchmark]
    public byte[] Hash_Empty() => _hasher.Hash(_emptyBody);

    [Benchmark]
    public byte[] Hash_1KB() => _hasher.Hash(_body1Kb);

    [Benchmark]
    public byte[] Hash_16KB() => _hasher.Hash(_body16Kb);

    [Benchmark]
    public byte[] Hash_256KB() => _hasher.Hash(_body256Kb);

    [Benchmark]
    public Fingerprint Compute_Fingerprint()
    {
        return _fingerprintProvider.Compute("POST", "/api/v1/resource", "scope:default", _headers, _bodyHash);
    }
}
