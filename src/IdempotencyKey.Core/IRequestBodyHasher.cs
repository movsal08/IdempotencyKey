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
        _maxBytes = maxBytes;
    }

    public byte[] Hash(byte[] body)
    {
        if (body == null || body.Length == 0) return Array.Empty<byte>();

        // If body is larger than maxBytes, hash first N bytes + length
        if (body.Length > _maxBytes)
        {
             // Strategy: Hash(First N bytes + BitConverter.GetBytes(Length))
             // This avoids collisions where the prefix is the same but the suffix differs,
             // but we don't want to read/hash the whole large body.
             // "hash only first N bytes + include length"

             var buffer = new byte[_maxBytes + sizeof(int)];
             Array.Copy(body, 0, buffer, 0, _maxBytes);
             var lenBytes = BitConverter.GetBytes(body.Length);
             Array.Copy(lenBytes, 0, buffer, _maxBytes, sizeof(int));

             return SHA256.HashData(buffer);
        }
        else
        {
            return SHA256.HashData(body);
        }
    }
}
