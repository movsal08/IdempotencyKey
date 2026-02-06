namespace IdempotencyKey.Core;

public record struct IdempotencyKey(string Scope, string Key)
{
    public override string ToString() => $"{Scope}:{Key}";
}

public static class IdempotencyScopes
{
    public const string Default = "global";
}
