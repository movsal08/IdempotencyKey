namespace IdempotencyKey.Core;

public enum IdempotencyEntryState
{
    InFlight = 0,
    Completed = 1
}

public enum TryBeginOutcome
{
    Acquired,
    AlreadyCompleted,
    InFlight,
    Conflict
}
