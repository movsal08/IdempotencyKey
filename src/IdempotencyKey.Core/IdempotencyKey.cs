namespace IdempotencyKey.Core;

public record struct IdempotencyKey(string Scope, string Key)
{
    private const char Separator = ':';
    private const char EscapeChar = '\\';

    public override string ToString() => $"{Escape(Scope)}{Separator}{Escape(Key)}";

    private static string Escape(string value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        // First escape the escape character itself, then escape the separator.
        return value
            .Replace(EscapeChar.ToString(), new string(EscapeChar, 2))
            .Replace(Separator.ToString(), $"{EscapeChar}{Separator}");
    }
}

public static class IdempotencyScopes
{
    public const string Default = "default";
}
