namespace IdempotencyKey.Core;

public interface IIdempotencyStore
{
    Task<TryBeginResult> TryBeginAsync(IdempotencyKey key, Fingerprint fingerprint, IdempotencyPolicy policy, CancellationToken ct);
    Task CompleteAsync(IdempotencyKey key, Fingerprint fingerprint, IdempotencyResponseSnapshot snapshot, TimeSpan ttl, CancellationToken ct);
    Task<IdempotencyResponseSnapshot?> TryGetCompletedAsync(IdempotencyKey key, CancellationToken ct);
}
