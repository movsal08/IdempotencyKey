using System.Security.Cryptography;
using System.Text;

namespace IdempotencyKey.Core;

public interface IFingerprintProvider
{
    Fingerprint Compute(string method, string routeTemplate, string scope, IDictionary<string, string[]>? selectedHeaders, byte[]? bodyHash);
}

public class Sha256FingerprintProvider : IFingerprintProvider
{
    public Fingerprint Compute(string method, string routeTemplate, string scope, IDictionary<string, string[]>? selectedHeaders, byte[]? bodyHash)
    {
        // Canonicalize
        // method|route|scope|headerKey=val1,val2|...|bodyHash
        var sb = new StringBuilder();
        sb.Append(method.ToUpperInvariant()).Append('|');
        sb.Append(routeTemplate).Append('|');
        sb.Append(scope).Append('|');

        if (selectedHeaders != null)
        {
            foreach (var kvp in selectedHeaders.OrderBy(k => k.Key))
            {
                sb.Append(kvp.Key.ToLowerInvariant()).Append('=');
                sb.Append(string.Join(',', kvp.Value.OrderBy(v => v)));
                sb.Append('|');
            }
        }

        if (bodyHash != null)
        {
            sb.Append(Convert.ToHexString(bodyHash));
        }

        var inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(inputBytes);
        return new Fingerprint(Convert.ToHexString(hash));
    }
}
