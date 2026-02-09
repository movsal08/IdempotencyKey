namespace IdempotencyKey.Core;

public record TryBeginResult(
    TryBeginOutcome Outcome,
    IdempotencyResponseSnapshot? Snapshot = null,
    TimeSpan? RetryAfter = null,
    string? ConflictReason = null
);
