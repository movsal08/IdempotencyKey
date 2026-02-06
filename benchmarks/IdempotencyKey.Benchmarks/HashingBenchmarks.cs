using BenchmarkDotNet.Attributes;
using IdempotencyKey.Core;
using System.Security.Cryptography;
using System.Text;

namespace Benchmarks;

[MemoryDiagnoser]
public class HashingBenchmarks
{
    private Sha256FingerprintProvider _fingerprintProvider = null!;
    private DefaultRequestBodyHasher _bodyHasher = null!;

    // Data for Fingerprint benchmarks
    private Dictionary<string, string[]> _headersSmall = null!;
    private Dictionary<string, string[]> _headersLarge = null!;
    private byte[] _smallBodyHash = null!;

    // Data for BodyHasher benchmarks
    private byte[] _payload0B = null!;
    private byte[] _payload1KB = null!;
    private byte[] _payload16KB = null!;
    private byte[] _payload256KB = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fingerprintProvider = new Sha256FingerprintProvider();
        _bodyHasher = new DefaultRequestBodyHasher();

        // Setup headers
        _headersSmall = new Dictionary<string, string[]>();
        for (int i = 0; i < 5; i++)
        {
            _headersSmall[$"Header-{i}"] = new[] { $"Value-{i}" };
        }

        _headersLarge = new Dictionary<string, string[]>();
        for (int i = 0; i < 20; i++)
        {
            _headersLarge[$"Header-{i}"] = new[] { $"Value-{i}" };
        }

        // Setup body hash (small, e.g., SHA256 size is 32 bytes)
        _smallBodyHash = new byte[32];
        Random.Shared.NextBytes(_smallBodyHash);

        // Setup payloads
        _payload0B = Array.Empty<byte>();
        _payload1KB = new byte[1024];
        Random.Shared.NextBytes(_payload1KB);
        _payload16KB = new byte[16 * 1024];
        Random.Shared.NextBytes(_payload16KB);
        _payload256KB = new byte[256 * 1024];
        Random.Shared.NextBytes(_payload256KB);
    }

    [Benchmark]
    public Fingerprint Fingerprint_NoHeaders_NoBodyHash()
    {
        return _fingerprintProvider.Compute("POST", "/api/test", "scope1", null, null);
    }

    [Benchmark]
    public Fingerprint Fingerprint_5Headers_SmallBodyHash()
    {
        return _fingerprintProvider.Compute("POST", "/api/test", "scope1", _headersSmall, _smallBodyHash);
    }

    [Benchmark]
    public Fingerprint Fingerprint_20Headers_SmallBodyHash()
    {
        return _fingerprintProvider.Compute("POST", "/api/test", "scope1", _headersLarge, _smallBodyHash);
    }

    [Benchmark]
    public byte[] BodyHash_0B()
    {
        return _bodyHasher.Hash(_payload0B);
    }

    [Benchmark]
    public byte[] BodyHash_1KB()
    {
        return _bodyHasher.Hash(_payload1KB);
    }

    [Benchmark]
    public byte[] BodyHash_16KB()
    {
        return _bodyHasher.Hash(_payload16KB);
    }

    [Benchmark]
    public byte[] BodyHash_256KB()
    {
        return _bodyHasher.Hash(_payload256KB);
    }
}
