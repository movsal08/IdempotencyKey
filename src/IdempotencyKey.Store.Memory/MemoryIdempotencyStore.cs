using System.Collections.Concurrent;
using IdempotencyKey.Core;

namespace IdempotencyKey.Store.Memory;

using IdempotencyKeyType = IdempotencyKey.Core.IdempotencyKey;

public class MemoryIdempotencyStore : IIdempotencyStore, IDisposable
{
    private class Entry
    {
        public Fingerprint Fingerprint { get; set; }
        public IdempotencyEntryState State { get; set; }
        public DateTimeOffset LeaseUntilUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public IdempotencyResponseSnapshot? Snapshot { get; set; }
    }

    private readonly ConcurrentDictionary<IdempotencyKeyType, Entry> _store = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public MemoryIdempotencyStore()
    {
        // Cleanup every minute
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void Cleanup(object? state)
    {
        if (_disposed) return;

        var now = DateTimeOffset.UtcNow;
        // Cast to ICollection to access atomic Remove(KeyValuePair) which ensures we only remove if the value matches
        var collection = (ICollection<KeyValuePair<IdempotencyKeyType, Entry>>)_store;

        foreach (var kvp in _store)
        {
            if (kvp.Value.ExpiresAtUtc < now)
            {
                collection.Remove(kvp);
            }
        }
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
        while(true)
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

                 // Update snapshot metadata
                 snapshot.CreatedAtUtc = now;
                 snapshot.ExpiresAtUtc = now.Add(ttl);

                 var completedEntry = new Entry
                 {
                     Fingerprint = currentEntry.Fingerprint,
                     State = IdempotencyEntryState.Completed,
                     LeaseUntilUtc = DateTimeOffset.MinValue,
                     ExpiresAtUtc = now.Add(ttl),
                     Snapshot = snapshot
                 };

                 if (_store.TryUpdate(key, completedEntry, currentEntry))
                 {
                     break;
                 }
                 // Retry if update failed
            }
            else
            {
                // Entry gone. Recreate as completed.
                var now = DateTimeOffset.UtcNow;

                 // Update snapshot metadata
                 snapshot.CreatedAtUtc = now;
                 snapshot.ExpiresAtUtc = now.Add(ttl);

                 var completedEntry = new Entry
                 {
                     Fingerprint = fingerprint,
                     State = IdempotencyEntryState.Completed,
                     LeaseUntilUtc = DateTimeOffset.MinValue,
                     ExpiresAtUtc = now.Add(ttl),
                     Snapshot = snapshot
                 };
                 if (_store.TryAdd(key, completedEntry))
                 {
                     break;
                 }
                 // Retry if add failed
            }
        }

        return Task.CompletedTask;
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
}
