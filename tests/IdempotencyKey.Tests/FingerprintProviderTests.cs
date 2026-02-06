using IdempotencyKey.Core;
using System.Text;

namespace IdempotencyKey.Tests;

public class FingerprintProviderTests
{
    [Fact]
    public void Compute_ReturnsDeterministicHash()
    {
        var provider = new Sha256FingerprintProvider();
        var headers = new Dictionary<string, string[]> { { "header1", ["value1"] } };
        var bodyHash = new byte[] { 1, 2, 3 };

        var f1 = provider.Compute("POST", "/api/test", "scope1", headers, bodyHash);
        var f2 = provider.Compute("POST", "/api/test", "scope1", headers, bodyHash);

        Assert.Equal(f1, f2);
    }

    [Fact]
    public void Compute_DifferentInput_ReturnsDifferentHash()
    {
        var provider = new Sha256FingerprintProvider();
        var headers = new Dictionary<string, string[]> { { "header1", ["value1"] } };
        var bodyHash = new byte[] { 1, 2, 3 };

        var f1 = provider.Compute("POST", "/api/test", "scope1", headers, bodyHash);
        var f2 = provider.Compute("POST", "/api/test", "scope2", headers, bodyHash); // Diff scope

        Assert.NotEqual(f1, f2);
    }

    [Fact]
    public void Compute_WithNullHeadersOrBody_Works()
    {
        var provider = new Sha256FingerprintProvider();
        var f1 = provider.Compute("POST", "/api/test", "scope1", null, null);
        Assert.NotNull(f1.Value);
        Assert.NotEmpty(f1.Value);
    }
}
