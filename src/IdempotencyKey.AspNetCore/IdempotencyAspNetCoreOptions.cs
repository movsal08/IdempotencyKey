using IdempotencyKey.Core;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyAspNetCoreOptions : IdempotencyKeyOptions
{
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
}
