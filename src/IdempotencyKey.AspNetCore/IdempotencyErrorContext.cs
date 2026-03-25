namespace IdempotencyKey.AspNetCore;

public enum IdempotencyErrorKind
{
    Validation,
    Conflict,
    InFlight,
    InFlightTimeout
}

public sealed record IdempotencyErrorContext(
    IdempotencyErrorKind Kind,
    int StatusCode,
    string Message,
    string? ConflictReason = null,
    double? RetryAfterSeconds = null);
