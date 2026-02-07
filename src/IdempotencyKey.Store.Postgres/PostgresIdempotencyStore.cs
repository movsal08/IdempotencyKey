using System.Data;
using System.Text.Json;
using IdempotencyKey.Core;
using Npgsql;
using NpgsqlTypes;

namespace IdempotencyKey.Store.Postgres;

public class PostgresIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly PostgresIdempotencyStoreOptions _options;
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private bool _disposed;

    public PostgresIdempotencyStore(PostgresIdempotencyStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.DataSource != null)
        {
            _dataSource = options.DataSource;
            _ownsDataSource = false;
        }
        else if (!string.IsNullOrEmpty(options.ConnectionString))
        {
            _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
            _ownsDataSource = true;
        }
        else
        {
            throw new ArgumentException("Either DataSource or ConnectionString must be provided in PostgresIdempotencyStoreOptions.");
        }
    }

    public async Task<TryBeginResult> TryBeginAsync(IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, IdempotencyPolicy policy, CancellationToken ct)
    {
        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            try
            {
                var now = DateTimeOffset.UtcNow;
                var leaseUntil = now.Add(policy.LeaseDuration);
                var expiresAt = now.Add(policy.Ttl);

                // 1. Try to insert (Optimistic: New Key)
                const string insertSql = @"
                    INSERT INTO {0} (scope, key, fingerprint, state, lease_until_utc, expires_at_utc, created_at_utc, updated_at_utc)
                    VALUES (@s, @k, @fp, 'inflight', @lu, @exp, @now, @now)
                    ON CONFLICT (scope, key) DO NOTHING";

                var formattedInsertSql = string.Format(insertSql, GetTableName());

                using (var cmd = new NpgsqlCommand(formattedInsertSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("s", key.Scope);
                    cmd.Parameters.AddWithValue("k", key.Key);
                    cmd.Parameters.AddWithValue("fp", fingerprint.Value);
                    cmd.Parameters.AddWithValue("lu", leaseUntil);
                    cmd.Parameters.AddWithValue("exp", expiresAt);
                    cmd.Parameters.AddWithValue("now", now);

                    int rows = await cmd.ExecuteNonQueryAsync(ct);
                    if (rows > 0)
                    {
                        await tx.CommitAsync(ct);
                        return new TryBeginResult(TryBeginOutcome.Acquired);
                    }
                }

                // 2. Row exists (or existed). Lock it and inspect.
                const string selectSql = @"
                    SELECT fingerprint, state, lease_until_utc, expires_at_utc, snapshot_json
                    FROM {0}
                    WHERE scope = @s AND key = @k
                    FOR UPDATE";

                var formattedSelectSql = string.Format(selectSql, GetTableName());

                string? storedFingerprint = null;
                string? storedState = null;
                DateTimeOffset storedLeaseUntil = default;
                DateTimeOffset storedExpiresAt = default;
                string? storedSnapshotJson = null;
                bool found = false;

                using (var cmd = new NpgsqlCommand(formattedSelectSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("s", key.Scope);
                    cmd.Parameters.AddWithValue("k", key.Key);

                    using (var reader = await cmd.ExecuteReaderAsync(ct))
                    {
                        if (await reader.ReadAsync(ct))
                        {
                            found = true;
                            storedFingerprint = reader.GetString(0);
                            storedState = reader.GetString(1);
                            storedLeaseUntil = reader.GetFieldValue<DateTimeOffset>(2);
                            storedExpiresAt = reader.GetFieldValue<DateTimeOffset>(3);
                            if (!reader.IsDBNull(4))
                            {
                                storedSnapshotJson = reader.GetString(4);
                            }
                        }
                    }
                }

                if (!found)
                {
                    // Row disappeared (deleted or expired/cleaned up) between INSERT attempt and SELECT.
                    // Retry loop will attempt INSERT again.
                    await tx.RollbackAsync(ct);
                    continue;
                }

                // 3. Logic checks
                // Check Expiry
                if (storedExpiresAt < now)
                {
                    // Treat as expired/missing -> Overwrite to InFlight
                    await UpdateToInflightAsync(conn, tx, key, fingerprint, leaseUntil, expiresAt, now, ct);
                    await tx.CommitAsync(ct);
                    return new TryBeginResult(TryBeginOutcome.Acquired);
                }

                // Check Fingerprint
                if (storedFingerprint != fingerprint.Value)
                {
                    await tx.CommitAsync(ct);
                    return new TryBeginResult(TryBeginOutcome.Conflict, ConflictReason: "Fingerprint mismatch");
                }

                // Check State
                if (storedState == "completed")
                {
                    await tx.CommitAsync(ct);
                    IdempotencyResponseSnapshot? snapshot = null;
                    if (!string.IsNullOrEmpty(storedSnapshotJson))
                    {
                        snapshot = JsonSerializer.Deserialize(storedSnapshotJson, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
                    }
                    return new TryBeginResult(TryBeginOutcome.AlreadyCompleted, Snapshot: snapshot);
                }

                if (storedState == "inflight")
                {
                    if (storedLeaseUntil < now)
                    {
                        // Lease expired -> Re-acquire
                        // Refresh expires_at as well
                        await UpdateToInflightAsync(conn, tx, key, fingerprint, leaseUntil, expiresAt, now, ct);
                        await tx.CommitAsync(ct);
                        return new TryBeginResult(TryBeginOutcome.Acquired);
                    }
                    else
                    {
                        // Active Lease
                        await tx.CommitAsync(ct);
                        var retryAfter = storedLeaseUntil - now;
                        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                        return new TryBeginResult(TryBeginOutcome.InFlight, RetryAfter: retryAfter);
                    }
                }

                throw new InvalidOperationException($"Unknown state: {storedState}");
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        throw new InvalidOperationException("Failed to acquire lock after multiple attempts.");
    }

    private async Task UpdateToInflightAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, DateTimeOffset leaseUntil, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        const string updateSql = @"
            UPDATE {0}
            SET state = 'inflight',
                fingerprint = @fp,
                lease_until_utc = @lu,
                expires_at_utc = @exp,
                snapshot_json = NULL,
                updated_at_utc = @now
            WHERE scope = @s AND key = @k";

        var formattedSql = string.Format(updateSql, GetTableName());

        using var cmd = new NpgsqlCommand(formattedSql, conn, tx);
        cmd.Parameters.AddWithValue("s", key.Scope);
        cmd.Parameters.AddWithValue("k", key.Key);
        cmd.Parameters.AddWithValue("fp", fingerprint.Value);
        cmd.Parameters.AddWithValue("lu", leaseUntil);
        cmd.Parameters.AddWithValue("exp", expiresAt);
        cmd.Parameters.AddWithValue("now", now);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, IdempotencyResponseSnapshot snapshot, TimeSpan ttl, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
             var now = DateTimeOffset.UtcNow;
             var expiresAt = now.Add(ttl);

             // Ensure snapshot metadata timestamps are populated before serialization
             if (snapshot.CreatedAtUtc == default)
             {
                 snapshot.CreatedAtUtc = now;
             }
             if (snapshot.ExpiresAtUtc == default)
             {
                 snapshot.ExpiresAtUtc = expiresAt;
             }
             var snapshotJson = JsonSerializer.Serialize(snapshot, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);

             // UPSERT: Insert if missing, Update if exists (and matches fingerprint)
             const string sql = @"
                INSERT INTO {0} (scope, key, fingerprint, state, lease_until_utc, expires_at_utc, created_at_utc, updated_at_utc, snapshot_json)
                VALUES (@s, @k, @fp, 'completed', @now, @exp, @now, @now, @json::jsonb)
                ON CONFLICT (scope, key) DO UPDATE
                SET state = 'completed',
                    snapshot_json = @json::jsonb,
                    expires_at_utc = @exp,
                    updated_at_utc = @now
                WHERE {0}.fingerprint = @fp";

             var formattedSql = string.Format(sql, GetTableName());

             using var cmd = new NpgsqlCommand(formattedSql, conn, tx);
             cmd.Parameters.AddWithValue("s", key.Scope);
             cmd.Parameters.AddWithValue("k", key.Key);
             cmd.Parameters.AddWithValue("fp", fingerprint.Value);
             cmd.Parameters.AddWithValue("json", snapshotJson);
             cmd.Parameters.AddWithValue("exp", expiresAt);
             cmd.Parameters.AddWithValue("now", now);

             int rows = await cmd.ExecuteNonQueryAsync(ct);

             if (rows == 0)
             {
                 // Row existed but fingerprint check in WHERE clause failed.
                 throw new InvalidOperationException("Fingerprint mismatch during completion.");
             }

             await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IdempotencyResponseSnapshot?> TryGetCompletedAsync(IdempotencyKey.Core.IdempotencyKey key, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string sql = @"
            SELECT state, snapshot_json, expires_at_utc
            FROM {0}
            WHERE scope = @s AND key = @k";

        var formattedSql = string.Format(sql, GetTableName());

        using var cmd = new NpgsqlCommand(formattedSql, conn);
        cmd.Parameters.AddWithValue("s", key.Scope);
        cmd.Parameters.AddWithValue("k", key.Key);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var state = reader.GetString(0);
            var expiresAt = reader.GetFieldValue<DateTimeOffset>(2);

            // Check expiry
            if (expiresAt < DateTimeOffset.UtcNow)
            {
                return null;
            }

            if (state == "completed" && !reader.IsDBNull(1))
            {
                var json = reader.GetString(1);
                return JsonSerializer.Deserialize(json, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
            }
        }

        return null;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (!_options.EnableEnsureCreated) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var fullTableName = GetTableName();

        var sql = $@"
            CREATE TABLE IF NOT EXISTS {fullTableName} (
                scope text NOT NULL,
                key text NOT NULL,
                fingerprint text NOT NULL,
                state text NOT NULL,
                lease_until_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL,
                snapshot_json jsonb NULL,
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                updated_at_utc timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (scope, key)
            );

            CREATE INDEX IF NOT EXISTS ""IX_{_options.TableName}_expires_at_utc"" ON {fullTableName} (expires_at_utc);
        ";

        using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string GetTableName()
    {
        return $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(_options.TableName)}";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsDataSource && _dataSource is IDisposable d)
        {
            d.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
