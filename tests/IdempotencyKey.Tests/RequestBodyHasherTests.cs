using IdempotencyKey.Core;

namespace IdempotencyKey.Tests;

public class RequestBodyHasherTests
{
    [Fact]
    public void Hash_SmallBody_HashesAll()
    {
        var hasher = new DefaultRequestBodyHasher(100);
        var body = new byte[] { 1, 2, 3 };
        var hash = hasher.Hash(body);

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
    }

    [Fact]
    public void Hash_BodyExceedingLimit_Throws()
    {
        var hasher = new DefaultRequestBodyHasher(5);
        var body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; // Length 10

        var ex = Assert.Throws<InvalidOperationException>(() => hasher.Hash(body));
        Assert.Contains("exceeded maximum hash size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_BodiesWithinLimit_HashesFullBody_NoCollisions()
    {
        var hasher = new DefaultRequestBodyHasher(32);
        var body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        var hash1 = hasher.Hash(body);

        // Create another body with same prefix but different suffix (length same)
        var body2 = new byte[] { 1, 2, 3, 4, 5, 0, 0, 0, 0, 0 }; // Length 10
        var hash2 = hasher.Hash(body2);

        Assert.NotEqual(hash1, hash2);

        // Create body with same prefix but different length
        var body3 = new byte[] { 1, 2, 3, 4, 5, 6 }; // Length 6
        var hash3 = hasher.Hash(body3);

        Assert.NotEqual(hash1, hash3);
    }
}
