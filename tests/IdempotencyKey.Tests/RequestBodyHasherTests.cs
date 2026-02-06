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
    public void Hash_LargeBody_HashesPrefixAndLength()
    {
        var hasher = new DefaultRequestBodyHasher(5);
        var body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; // Length 10 > 5

        var hash1 = hasher.Hash(body);

        // Create another body with same prefix but different suffix (length same)
        var body2 = new byte[] { 1, 2, 3, 4, 5, 0, 0, 0, 0, 0 }; // Length 10
        // Expected: Hash should be SAME because we only hash prefix + length

        var hash2 = hasher.Hash(body2);

        Assert.Equal(hash1, hash2);

        // Create body with same prefix but different length
        var body3 = new byte[] { 1, 2, 3, 4, 5, 6 }; // Length 6
        var hash3 = hasher.Hash(body3);

        Assert.NotEqual(hash1, hash3);
    }
}
