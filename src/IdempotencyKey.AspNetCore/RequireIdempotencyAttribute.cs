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

    // Helper to convert to internal metadata
    internal IdempotencyPolicyMetadata ToMetadata()
    {
        return new IdempotencyPolicyMetadata
        {
            Ttl = TtlSeconds > 0 ? TimeSpan.FromSeconds(TtlSeconds) : null,
            WaitTimeout = WaitTimeoutSeconds > 0 ? TimeSpan.FromSeconds(WaitTimeoutSeconds) : null,
            RetryAfterSeconds = RetryAfterSeconds > 0 ? RetryAfterSeconds : null,
            MaxSnapshotBytes = MaxSnapshotBytes > 0 ? MaxSnapshotBytes : null,
            InFlightMode = InFlightMode,
            EnforcedByFilter = false // Attributes usually imply Middleware enforcement
        };
    }
}
