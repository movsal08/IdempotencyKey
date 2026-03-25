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
