using IdempotencyKey.Core;

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

    [Fact]
    public void Compute_HeaderKeyCasing_IsCanonicalized()
    {
        var provider = new Sha256FingerprintProvider();
        var bodyHash = new byte[] { 1, 2, 3 };

        var headersUpper = new Dictionary<string, string[]>
        {
            ["X-Tenant"] = ["alpha"]
        };

        var headersLower = new Dictionary<string, string[]>
        {
            ["x-tenant"] = ["alpha"]
        };

        var f1 = provider.Compute("POST", "/api/test", "scope1", headersUpper, bodyHash);
        var f2 = provider.Compute("POST", "/api/test", "scope1", headersLower, bodyHash);

        Assert.Equal(f1, f2);
    }

    [Fact]
    public void Compute_EscapesDelimitersAcrossComponents()
    {
        var provider = new Sha256FingerprintProvider();
        var bodyHash = new byte[] { 1, 2, 3 };

        var h1 = new Dictionary<string, string[]>
        {
            ["x-meta"] = ["a|b=c,d"]
        };

        var h2 = new Dictionary<string, string[]>
        {
            ["x-meta"] = ["a\\|b\\=c\\,d"]
        };

        var f1 = provider.Compute("PO|ST", "/api/tes=t", "sc,ope", h1, bodyHash);
        var f2 = provider.Compute("PO|ST", "/api/tes=t", "sc,ope", h2, bodyHash);

        Assert.NotEqual(f1, f2);
    }
}
