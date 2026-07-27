using System;

namespace IdempotencyKey.AspNetCore;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireIdempotencyAttribute : Attribute
{
    public double TtlSeconds { get; set; }
    public double WaitTimeoutSeconds { get; set; }
    public double RetryAfterSeconds { get; set; }
    public int MaxSnapshotBytes { get; set; }
    public InFlightMode InFlightMode { get; set; } = InFlightMode.Wait;

    /// <summary>
    /// Restricts idempotency to these HTTP methods for the decorated controller/action. When not
    /// set, the globally configured <c>ApplicableMethods</c> (POST/PUT/PATCH/DELETE by default)
    /// apply, so read endpoints under a class-level attribute are never affected.
    /// </summary>
    public string[]? Methods { get; set; }

    // Helper to convert to internal metadata
    internal IdempotencyPolicyMetadata ToMetadata()
    {
        return new IdempotencyPolicyMetadata
        {
            Methods = Methods,
            Ttl = TtlSeconds > 0 ? TimeSpan.FromSeconds(TtlSeconds) : null,
            WaitTimeout = WaitTimeoutSeconds > 0 ? TimeSpan.FromSeconds(WaitTimeoutSeconds) : null,
            RetryAfterSeconds = RetryAfterSeconds > 0 ? RetryAfterSeconds : null,
            MaxSnapshotBytes = MaxSnapshotBytes > 0 ? MaxSnapshotBytes : null,
            InFlightMode = InFlightMode,
            EnforcedByFilter = false // Attributes usually imply Middleware enforcement
        };
    }
}
