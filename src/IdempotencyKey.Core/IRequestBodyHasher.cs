using System.Security.Cryptography;

namespace IdempotencyKey.Core;

public interface IRequestBodyHasher
{
    byte[] Hash(byte[] body);
}

public class DefaultRequestBodyHasher : IRequestBodyHasher
{
    // Keeping constructor for compatibility if we want to add limits later,
    // but ignoring maxBytes for now to enforce full safety as requested.
    public DefaultRequestBodyHasher(int maxBytes)
    {
    }

    public DefaultRequestBodyHasher()
    {
    }

    public byte[] Hash(byte[] body)
    {
        if (body == null || body.Length == 0) return Array.Empty<byte>();
        return SHA256.HashData(body);
    }
}
