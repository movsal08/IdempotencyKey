namespace IdempotencyKey.Core;

public record struct Fingerprint(string Value)
{
    public override string ToString() => Value;
}
