using Microsoft.AspNetCore.Http;

namespace IdempotencyKey.AspNetCore;

/// <summary>
/// Central HTTP-method gate. Idempotency only ever applies to state-changing methods, regardless of
/// how the endpoint opted in (global predicate, <c>[RequireIdempotency]</c> attribute, or the
/// minimal-API convention). This keeps a class-level attribute on a controller from demanding an
/// idempotency key on that controller's read endpoints.
/// </summary>
internal static class IdempotencyMethodGate
{
    public static bool IsApplicable(string method, IdempotencyPolicyMetadata? metadata, IdempotencyAspNetCoreOptions options)
    {
        IReadOnlyCollection<string> allowed = metadata?.Methods ?? options.ApplicableMethods;

        // An explicitly empty override means "no method restriction" — opt out of the gate.
        if (allowed.Count == 0)
        {
            return true;
        }

        foreach (var allowedMethod in allowed)
        {
            if (string.Equals(allowedMethod, method, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesGlobalPredicate(HttpContext context, IdempotencyAspNetCoreOptions options)
    {
        return options.Predicate?.Invoke(context) ?? IsApplicable(context.Request.Method, metadata: null, options);
    }
}
