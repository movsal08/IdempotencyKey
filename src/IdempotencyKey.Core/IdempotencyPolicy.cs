namespace IdempotencyKey.Core;

public class IdempotencyPolicy
{
    public TimeSpan LeaseDuration { get; set; }
    public TimeSpan Ttl { get; set; }
}
