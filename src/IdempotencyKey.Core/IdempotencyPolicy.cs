namespace IdempotencyKey.Core;

public class IdempotencyPolicy
{
    public TimeSpan LeaseDuration { get; set; }
    public TimeSpan Ttl { get; set; }

    public void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new System.ArgumentOutOfRangeException(nameof(LeaseDuration), "LeaseDuration must be a positive TimeSpan.");
        }

        if (Ttl <= TimeSpan.Zero)
        {
            throw new System.ArgumentOutOfRangeException(nameof(Ttl), "Ttl must be a positive TimeSpan.");
        }

        if (Ttl < LeaseDuration)
        {
            throw new System.ArgumentException("Ttl must be greater than or equal to LeaseDuration.", nameof(Ttl));
        }
    }
}
