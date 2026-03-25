using IdempotencyKey.Core;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyAspNetCoreOptions : IdempotencyKeyOptions
{
    private const string UnreservedChars = "-._~";

    /// <summary>
    /// Function to determine the scope for the idempotency key. Defaults to "default".
    /// </summary>
    public Func<HttpContext, string> ScopeProvider { get; set; } = _ => IdempotencyScopes.Default;

    /// <summary>
    /// Predicate to determine if idempotency should be applied to a request globally.
    /// Defaults to true for POST, PUT, PATCH, DELETE.
    /// Note: Endpoint specific metadata overrides this.
    /// </summary>
    public Func<HttpContext, bool> Predicate { get; set; } = req =>
        HttpMethods.IsPost(req.Request.Method) ||
        HttpMethods.IsPut(req.Request.Method) ||
        HttpMethods.IsPatch(req.Request.Method) ||
        HttpMethods.IsDelete(req.Request.Method);

    /// <summary>
    /// List of headers to include in the fingerprint.
    /// </summary>
    public List<string> HeaderKeysToIncludeInFingerprint { get; set; } = new();

    /// <summary>
    /// Maximum accepted length for Idempotency-Key values.
    /// </summary>
    public int MaxIdempotencyKeyLength { get; set; } = 256;

    /// <summary>
    /// Maximum request body size that will be hashed for fingerprinting.
    /// Requests exceeding this value fail with 400.
    /// </summary>
    public int MaxRequestBodyHashBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Validates a key using RFC3986 unreserved characters.
    /// </summary>
    public Func<string, bool> KeyValidator { get; set; } = static key =>
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
            {
                continue;
            }

            if (UnreservedChars.IndexOf(ch) >= 0)
            {
                continue;
            }

            return false;
        }

        return true;
    };
}
