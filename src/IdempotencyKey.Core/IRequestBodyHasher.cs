using System.Security.Cryptography;

namespace IdempotencyKey.Core;

public interface IRequestBodyHasher
{
    byte[] Hash(byte[] body);
}

public class DefaultRequestBodyHasher : IRequestBodyHasher
{
    private readonly int _maxBytes;

    public DefaultRequestBodyHasher(int maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be greater than zero.");
        }

        _maxBytes = maxBytes;
    }

    public DefaultRequestBodyHasher()
        : this(1024 * 1024)
    {
    }

    public byte[] Hash(byte[] body)
    {
        if (body == null || body.Length == 0) return Array.Empty<byte>();
        if (body.Length > _maxBytes)
        {
            throw new InvalidOperationException($"Request body exceeded maximum hash size of {_maxBytes} bytes.");
        }

        return SHA256.HashData(body);
    }
}
