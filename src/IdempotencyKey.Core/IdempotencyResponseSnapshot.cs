namespace IdempotencyKey.Core;

public class IdempotencyResponseSnapshot
{
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public Dictionary<string, string[]> Headers { get; set; } = new();
    public byte[]? Body { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
