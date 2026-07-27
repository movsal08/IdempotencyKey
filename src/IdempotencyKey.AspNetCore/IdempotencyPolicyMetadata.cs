namespace IdempotencyKey.AspNetCore;

public class IdempotencyPolicyMetadata
{
    // If null, use global default
    public TimeSpan? Ttl { get; set; }
    public TimeSpan? LeaseDuration { get; set; }
    public TimeSpan? WaitTimeout { get; set; }
    public double? RetryAfterSeconds { get; set; }
    public int? MaxSnapshotBytes { get; set; }

    // Mode for handling InFlight requests
    public InFlightMode? InFlightMode { get; set; }

    /// <summary>
    /// HTTP methods this endpoint's idempotency applies to. When null the global
    /// <c>IdempotencyAspNetCoreOptions.ApplicableMethods</c> set is used (POST/PUT/PATCH/DELETE by
    /// default). Set to an empty collection to apply idempotency to every method on this endpoint.
    /// </summary>
    public IReadOnlyCollection<string>? Methods { get; set; }

    // Internal flag to indicate that this policy is enforced by an Endpoint Filter
    internal bool EnforcedByFilter { get; set; }
}

public enum InFlightMode
{
    Wait,
    RetryAfter
}
