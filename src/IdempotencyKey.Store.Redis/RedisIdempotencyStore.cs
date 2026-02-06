using System.Text.Json;
using IdempotencyKey.Core;
using StackExchange.Redis;

namespace IdempotencyKey.Store.Redis;

public class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly RedisIdempotencyStoreOptions _options;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _db;

    // Scripts
    private const string TryBeginScript = @"
        local key = KEYS[1]
        local fingerprint = ARGV[1]
        local lease_duration_ms = tonumber(ARGV[2])
        local ttl_ms = tonumber(ARGV[3])

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
        local key = KEYS[1]
        local fingerprint = ARGV[1]
        local snapshot_json = ARGV[2]
        local ttl_ms = tonumber(ARGV[3])

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

    public RedisIdempotencyStore(RedisIdempotencyStoreOptions options)
    {
        _options = options;

        if (options.ConnectionMultiplexerFactory != null)
        {
            _multiplexer = options.ConnectionMultiplexerFactory().GetAwaiter().GetResult();
        }
        else if (!string.IsNullOrEmpty(options.Configuration))
        {
            _multiplexer = ConnectionMultiplexer.Connect(options.Configuration);
        }
        else
        {
             throw new ArgumentException("Either Configuration or ConnectionMultiplexerFactory must be provided in RedisIdempotencyStoreOptions.");
        }

        _db = _multiplexer.GetDatabase(options.Database ?? -1);
    }

    public async Task<TryBeginResult> TryBeginAsync(IdempotencyKey.Core.IdempotencyKey key, Fingerprint fingerprint, IdempotencyPolicy policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var redisKey = GetRedisKey(key);
        var leaseDurationMs = (long)policy.LeaseDuration.TotalMilliseconds;
        var ttlMs = (long)policy.Ttl.TotalMilliseconds;

        var result = await _db.ScriptEvaluateAsync(TryBeginScript,
            keys: new RedisKey[] { redisKey },
            values: new RedisValue[] { fingerprint.Value, leaseDurationMs, ttlMs });

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

        var redisKey = GetRedisKey(key);
        var snapshotJson = JsonSerializer.Serialize(snapshot, IdempotencyJsonContext.Default.IdempotencyResponseSnapshot);
        var ttlMs = (long)ttl.TotalMilliseconds;

        var result = await _db.ScriptEvaluateAsync(CompleteScript,
            keys: new RedisKey[] { redisKey },
            values: new RedisValue[] { fingerprint.Value, snapshotJson, ttlMs });

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
}
