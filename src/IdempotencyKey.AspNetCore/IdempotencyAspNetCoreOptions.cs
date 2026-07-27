using IdempotencyKey.Core;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyAspNetCoreOptions : IdempotencyKeyOptions
{
    private const string UnreservedChars = "-._~";

    private const string ConflictTypeUri = "https://tools.ietf.org/html/rfc7231#section-6.5.8";

    /// <summary>
    /// Function to determine the scope for the idempotency key. Defaults to "default".
    /// </summary>
    public Func<HttpContext, string> ScopeProvider { get; set; } = _ => IdempotencyScopes.Default;

    /// <summary>
    /// HTTP methods idempotency applies to. This gate is enforced for <em>every</em> opt-in path —
    /// including endpoints marked with <c>[RequireIdempotency]</c> or <c>RequireIdempotency()</c> —
    /// so a class-level attribute on a controller never forces an <c>Idempotency-Key</c> header onto
    /// its GET/HEAD/OPTIONS actions. Defaults to POST, PUT, PATCH, DELETE.
    /// Per-endpoint overrides are available via <see cref="IdempotencyPolicyMetadata.Methods"/>.
    /// </summary>
    public HashSet<string> ApplicableMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    };

    /// <summary>
    /// Predicate to determine if idempotency should be applied to a request globally, for endpoints
    /// that carry no idempotency metadata. Defaults to <see cref="ApplicableMethods"/> membership.
    /// Note: endpoint specific metadata overrides this, but never bypasses
    /// <see cref="ApplicableMethods"/>.
    /// </summary>
    public Func<HttpContext, bool>? Predicate { get; set; }

    /// <summary>
    /// List of headers to include in the fingerprint.
    /// </summary>
    public List<string> HeaderKeysToIncludeInFingerprint { get; set; } = new();

    /// <summary>
    /// Maximum accepted length for Idempotency-Key values.
    /// </summary>
    public int MaxIdempotencyKeyLength { get; set; } = 256;

    /// <summary>
    /// When true, only successful responses (HTTP status &lt; 400) are cached. Non-success
    /// responses release the in-flight entry instead of being stored, so a retry re-executes
    /// the request rather than replaying the failure. Defaults to false (cache every response).
    /// </summary>
    public bool CacheSuccessResponsesOnly { get; set; }

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

    /// <summary>
    /// Custom error writer for idempotency failures (validation, conflict, in-flight).
    /// Use this to return your own error model.
    /// </summary>
    public Func<HttpContext, IdempotencyErrorContext, Task> ErrorResponseWriter { get; set; } =
        static async (httpContext, error) =>
        {
            httpContext.Response.StatusCode = error.StatusCode;

            if (error.RetryAfterSeconds.HasValue)
            {
                httpContext.Response.Headers["Retry-After"] = ((int)error.RetryAfterSeconds.Value).ToString();
            }

            if (error.Kind == IdempotencyErrorKind.Conflict)
            {
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    type = ConflictTypeUri,
                    title = "Idempotency Conflict",
                    detail = error.ConflictReason ?? error.Message
                });

                return;
            }

            await httpContext.Response.WriteAsync(error.Message);
        };
}
