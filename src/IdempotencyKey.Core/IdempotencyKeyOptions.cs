namespace IdempotencyKey.Core;

public class IdempotencyKeyOptions
{
    public string HeaderName { get; set; } = "Idempotency-Key";
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan DefaultLease { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DefaultWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public double DefaultRetryAfterSeconds { get; set; } = 2;
    public int DefaultMaxSnapshotBytes { get; set; } = 256 * 1024; // 256KB
}
