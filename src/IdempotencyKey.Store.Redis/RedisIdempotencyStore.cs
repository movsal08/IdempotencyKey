using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using IdempotencyKey.Core;
using StackExchange.Redis;

namespace IdempotencyKey.Store.Redis;

public class RedisIdempotencyStore : IIdempotencyStore, IDisposable, IAsyncDisposable
{
    private RedisIdempotencyStoreOptions _options;
    private IConnectionMultiplexer _multiplexer;
    private IDatabase _db;
    private bool _ownMultiplexer;
    private bool _disposed;

    // Scripts using named parameters for LuaScript.Prepare
    private const string TryBeginScript = @"
        local key = @key
        local fingerprint = @fingerprint
        local lease_duration_ms = tonumber(@lease_duration_ms)
        local ttl_ms = tonumber(@ttl_ms)

        local time = redis.call('TIME')
        local now_ms = time[1] * 1000 + math.floor(time[2] / 1000)

        local state = redis.call('HGET', key, 'state')

        if not state then
            -- Missing, create inflight
            redis.call('HSET', key, 'state', 'inflight', 'fingerprint', fingerprint, 'leaseUntilUtcMs', now_ms + lease_duration_ms)
            redis.call('PEXPIRE', key, ttl_ms)
            return { 'acquired' }
        end

        local stored_fingerprint = redis.call('HGET', key, 'fingerprint')
        if stored_fingerprint ~= fingerprint then
            return { 'conflict' }
        end

        if state == 'completed' then
            local snapshot = redis.call('HGET', key, 'snapshotJson')
            return { 'completed', snapshot }
        end

        if state == 'inflight' then
            local lease_until = tonumber(redis.call('HGET', key, 'leaseUntilUtcMs') or '0')
            if now_ms > lease_until then
                -- Expired lease, re-acquire
                redis.call('HSET', key, 'leaseUntilUtcMs', now_ms + lease_duration_ms)
                redis.call('PEXPIRE', key, ttl_ms)
                return { 'acquired' }
            else
                -- Active lease
                local retry_after = lease_until - now_ms
                return { 'inflight', retry_after }
            end
        end

        return { 'error', 'Unknown state' }
    ";

    private const string CompleteScript = @"
        local key = @key
        local fingerprint = @fingerprint
        local snapshot_json = @snapshot_json
        local ttl_ms = tonumber(@ttl_ms)

        local state = redis.call('HGET', key, 'state')

        if state then
            local stored_fingerprint = redis.call('HGET', key, 'fingerprint')
            if stored_fingerprint ~= fingerprint then
                return 'conflict'
            end
        end

        -- Create or Update to completed
        redis.call('HSET', key, 'state', 'completed', 'fingerprint', fingerprint, 'snapshotJson', snapshot_json)
        redis.call('PEXPIRE', key, ttl_ms)
        return 'ok'
    ";

    private static LuaScript? _tryBeginLua;
    private static LuaScript? _completeLua;

    public RedisIdempotencyStore(RedisIdempotencyStoreOptions options)
    {
        if (options.ConnectionMultiplexerFactory != null)
        {
            throw new InvalidOperationException("ConnectionMultiplexerFactory is async. Use RedisIdempotencyStore.CreateAsync when providing a factory.");
        }

        if (options.ConnectionMultiplexer != null)
        {
            Initialize(options, options.ConnectionMultiplexer, ownMultiplexer: false);
            return;
        }

        if (!string.IsNullOrEmpty(options.Configuration))
        {
            Initialize(options, ConnectionMultiplexer.Connect(options.Configuration), ownMultiplexer: true);
            return;
        }

        throw new ArgumentException("Either Configuration, ConnectionMultiplexer, or ConnectionMultiplexerFactory must be provided in RedisIdempotencyStoreOptions.");
    }

    private RedisIdempotencyStore(RedisIdempotencyStoreOptions options, IConnectionMultiplexer multiplexer, bool ownMultiplexer)
    {
        Initialize(options, multiplexer, ownMultiplexer);
    }

    public static async Task<RedisIdempotencyStore> CreateAsync(RedisIdempotencyStoreOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (options.ConnectionMultiplexer != null)
        {
            return new RedisIdempotencyStore(options, options.ConnectionMultiplexer, ownMultiplexer: false);
        }

        if (options.ConnectionMultiplexerFactory != null)
        {
            var multiplexer = await options.ConnectionMultiplexerFactory().ConfigureAwait(false);
            return new RedisIdempotencyStore(options, multiplexer, ownMultiplexer: false);
        }

        return new RedisIdempotencyStore(options);
    }

    [MemberNotNull(nameof(_options), nameof(_multiplexer), nameof(_db))]
    private void Initialize(RedisIdempotencyStoreOptions options, IConnectionMultiplexer multiplexer, bool ownMultiplexer)
    {
        _options = options;
        _multiplexer = multiplexer;
        _ownMultiplexer = ownMultiplexer;

        _db = _multiplexer.GetDatabase(options.Database ?? -1);

        // Prepare scripts if not already loaded
        if (_tryBeginLua == null)
        {
            _tryBeginLua = LuaScript.Prepare(TryBeginScript);
        }
        if (_completeLua == null)
        {
            _completeLua = LuaScript.Prepare(CompleteScript);
        }
    }

    public async Task<TryBeginResult> TryBeginAsync(IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, IdempotencyPolicy policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var redisKey = (RedisKey)GetRedisKey(key);
        var leaseDurationMs = (long)policy.LeaseDuration.TotalMilliseconds;
        var ttlMs = (long)policy.Ttl.TotalMilliseconds;

        var result = await _tryBeginLua!.EvaluateAsync(_db, new
        {
            key = redisKey,
            fingerprint = fingerprint.Value,
            lease_duration_ms = leaseDurationMs,
            ttl_ms = ttlMs
        });

        if (result.IsNull)
        {
            throw new InvalidOperationException("Redis script returned null.");
        }

        var resultArray = (RedisResult[])result!;
        string? status = (string?)resultArray[0];

        if (status is null)
        {
            throw new InvalidOperationException("Redis script returned null status.");
        }

        switch (status)
        {
            case "acquired":
                return new TryBeginResult(TryBeginOutcome.Acquired);
            case "conflict":
                return new TryBeginResult(TryBeginOutcome.Conflict, ConflictReason: "Fingerprint mismatch");
            case "completed":
                var snapshotJson = (string?)resultArray[1];
                if (snapshotJson is null) throw new InvalidOperationException("Snapshot json is null.");
                var snapshot = JsonSerializer.Deserialize(snapshotJson, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
                return new TryBeginResult(TryBeginOutcome.AlreadyCompleted, Snapshot: snapshot);
            case "inflight":
                var retryAfterMs = (long)resultArray[1];
                var retryAfter = TimeSpan.FromMilliseconds(retryAfterMs);
                return new TryBeginResult(TryBeginOutcome.InFlight, RetryAfter: retryAfter);
            default:
                throw new InvalidOperationException($"Unknown status from Redis script: {status}");
        }
    }

    public async Task CompleteAsync(IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, IdempotencyResponseSnapshot snapshot, TimeSpan ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var redisKey = (RedisKey)GetRedisKey(key);
        var snapshotJson = JsonSerializer.Serialize(snapshot, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
        var ttlMs = (long)ttl.TotalMilliseconds;

        var result = await _completeLua!.EvaluateAsync(_db, new
        {
            key = redisKey,
            fingerprint = fingerprint.Value,
            snapshot_json = snapshotJson,
            ttl_ms = ttlMs
        });

        string? status = (string?)result;

        if (status == "conflict")
        {
            throw new InvalidOperationException("Fingerprint mismatch during completion.");
        }
    }

    public async Task<IdempotencyResponseSnapshot?> TryGetCompletedAsync(IdempotencyKey.Core.IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        var hashEntries = await _db.HashGetAsync(redisKey, new RedisValue[] { "state", "snapshotJson" });

        var state = (string?)hashEntries[0];
        var snapshotJson = (string?)hashEntries[1];

        if (state == "completed" && !string.IsNullOrEmpty(snapshotJson))
        {
            return JsonSerializer.Deserialize(snapshotJson, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
        }

        return null;
    }

    private string GetRedisKey(IdempotencyKey.Core.IdempotencyKey key)
    {
        return $"{_options.KeyPrefix}{key.Scope}:{key.Key}";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing && _ownMultiplexer)
        {
            _multiplexer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownMultiplexer)
        {
            await _multiplexer.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}
