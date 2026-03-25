using System.Collections.Concurrent;
using IdempotencyKey.Core;

namespace IdempotencyKey.Store.Memory;

using IdempotencyKeyType = IdempotencyKey.Core.IdempotencyKey;

public class MemoryIdempotencyStore : IIdempotencyStore, IDisposable
{
    private class Entry
    {
        public Fingerprint Fingerprint { get; init; }
        public IdempotencyEntryState State { get; init; }
        public DateTimeOffset LeaseUntilUtc { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
        public IdempotencyResponseSnapshot? Snapshot { get; init; }
    }

    private readonly ConcurrentDictionary<IdempotencyKeyType, Entry> _store = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxEntries;
    private bool _disposed;

    public MemoryIdempotencyStore(int maxEntries = 100_000, TimeSpan? cleanupInterval = null)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be greater than zero.");
        }

        _maxEntries = maxEntries;
        var interval = cleanupInterval ?? TimeSpan.FromSeconds(30);
        _cleanupTimer = new Timer(Cleanup, null, interval, interval);
    }

    private void Cleanup(object? state)
    {
        if (_disposed) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _store)
        {
            if (kvp.Value.ExpiresAtUtc < now)
            {
                _store.TryRemove(kvp.Key, out _);
            }
        }

        EvictOverflow();
    }

    public Task<TryBeginResult> TryBeginAsync(IdempotencyKeyType key, Fingerprint fingerprint, IdempotencyPolicy policy, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_store.TryGetValue(key, out var entry))
            {
                // 1. Check TTL Expiry
                if (entry.ExpiresAtUtc < now)
                {
                    // Expired. Treat as new.
                    var newEntry = new Entry
                    {
                        Fingerprint = fingerprint,
                        State = IdempotencyEntryState.InFlight,
                        LeaseUntilUtc = now.Add(policy.LeaseDuration),
                        ExpiresAtUtc = now.Add(policy.Ttl)
                    };

                    if (_store.TryUpdate(key, newEntry, entry))
                    {
                        return Task.FromResult(new TryBeginResult(TryBeginOutcome.Acquired));
                    }
                    else
                    {
                        continue; // Retry loop
                    }
                }

                // 2. Check Conflict (Fingerprint mismatch)
                if (entry.Fingerprint.Value != fingerprint.Value)
                {
                    return Task.FromResult(new TryBeginResult(TryBeginOutcome.Conflict, ConflictReason: "Fingerprint mismatch"));
                }

                // 3. State Analysis
                if (entry.State == IdempotencyEntryState.Completed)
                {
                    return Task.FromResult(new TryBeginResult(TryBeginOutcome.AlreadyCompleted, Snapshot: entry.Snapshot));
                }
                else if (entry.State == IdempotencyEntryState.InFlight)
                {
                    if (entry.LeaseUntilUtc < now)
                    {
                        // Lease expired. Re-acquire.
                        var newEntry = new Entry
                        {
                            Fingerprint = fingerprint,
                            State = IdempotencyEntryState.InFlight,
                            LeaseUntilUtc = now.Add(policy.LeaseDuration),
                            ExpiresAtUtc = now.Add(policy.Ttl) // Reset TTL as well
                        };

                        if (_store.TryUpdate(key, newEntry, entry))
                        {
                            return Task.FromResult(new TryBeginResult(TryBeginOutcome.Acquired));
                        }
                        else
                        {
                            continue; // Retry loop
                        }
                    }
                    else
                    {
                        // Lease active.
                        var retryAfter = entry.LeaseUntilUtc - now;
                        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                        return Task.FromResult(new TryBeginResult(TryBeginOutcome.InFlight, RetryAfter: retryAfter));
                    }
                }
            }
            else
            {
                // New entry
                var newEntry = new Entry
                {
                    Fingerprint = fingerprint,
                    State = IdempotencyEntryState.InFlight,
                    LeaseUntilUtc = now.Add(policy.LeaseDuration),
                    ExpiresAtUtc = now.Add(policy.Ttl)
                };

                if (_store.TryAdd(key, newEntry))
                {
                    EvictOverflow();
                    return Task.FromResult(new TryBeginResult(TryBeginOutcome.Acquired));
                }
                else
                {
                    continue; // Retry loop
                }
            }
        }
    }

    public Task CompleteAsync(IdempotencyKeyType key, Fingerprint fingerprint, IdempotencyResponseSnapshot snapshot, TimeSpan ttl, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_store.TryGetValue(key, out var currentEntry))
            {
                // Check fingerprint
                if (currentEntry.Fingerprint.Value != fingerprint.Value)
                {
                    throw new InvalidOperationException("Fingerprint mismatch during completion.");
                }

                var now = DateTimeOffset.UtcNow;
                var completedSnapshot = CloneSnapshotWithMetadata(snapshot, now, now.Add(ttl));

                var completedEntry = new Entry
                {
                    Fingerprint = currentEntry.Fingerprint,
                    State = IdempotencyEntryState.Completed,
                    LeaseUntilUtc = DateTimeOffset.MinValue,
                    ExpiresAtUtc = now.Add(ttl),
                    Snapshot = completedSnapshot
                };

                if (_store.TryUpdate(key, completedEntry, currentEntry))
                {
                    break;
                }
                // Retry if update failed
            }
            else
            {
                throw new InvalidOperationException("Idempotency entry was not found during completion.");
            }
        }

        return Task.CompletedTask;
    }

    private static IdempotencyResponseSnapshot CloneSnapshotWithMetadata(
        IdempotencyResponseSnapshot snapshot,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        return new IdempotencyResponseSnapshot
        {
            StatusCode = snapshot.StatusCode,
            ContentType = snapshot.ContentType,
            Headers = snapshot.Headers.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            Body = snapshot.Body?.ToArray(),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public Task<IdempotencyResponseSnapshot?> TryGetCompletedAsync(IdempotencyKeyType key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                return Task.FromResult<IdempotencyResponseSnapshot?>(null);
            }

            if (entry.State == IdempotencyEntryState.Completed)
            {
                return Task.FromResult(entry.Snapshot);
            }
        }
        return Task.FromResult<IdempotencyResponseSnapshot?>(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cleanupTimer.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EvictOverflow()
    {
        var overflow = _store.Count - _maxEntries;
        if (overflow <= 0)
        {
            return;
        }

        var candidates = _store
            .OrderBy(static kvp => kvp.Value.ExpiresAtUtc)
            .Take(overflow)
            .Select(static kvp => kvp.Key)
            .ToArray();

        foreach (var key in candidates)
        {
            _store.TryRemove(key, out _);
        }
    }
}
