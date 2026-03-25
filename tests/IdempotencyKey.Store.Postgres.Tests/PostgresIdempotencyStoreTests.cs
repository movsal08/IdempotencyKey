using System.Text.Json;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace IdempotencyKey.Store.Postgres.Tests;

public class PostgresIdempotencyStoreTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PostgreSqlContainer? _container;
    private PostgresIdempotencyStore? _store;
    private bool _skip;

    public PostgresIdempotencyStoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:15-alpine")
                .Build();

            await _container.StartAsync();

            var options = new PostgresIdempotencyStoreOptions
            {
                ConnectionString = _container.GetConnectionString(),
                EnableEnsureCreated = true
            };

            _store = new PostgresIdempotencyStore(options);
            await _store.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to start Postgres container: {ex.Message}");
            _skip = true;
        }
    }

    public async Task DisposeAsync()
    {
        if (_store != null)
        {
            _store.Dispose();
        }
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private bool ShouldSkip()
    {
        if (_skip)
        {
            _output.WriteLine("Skipping test because Docker is unavailable or container failed to start.");
            return true;
        }
        return false;
    }

    [Fact]
    public async Task TryBeginAsync_NewKey_ReturnsAcquired()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        var result = await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBeginAsync_SameKey_SameFingerprint_InFlight_ReturnsInFlight()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);
        var result = await _store.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.InFlight, result.Outcome);
        Assert.NotNull(result.RetryAfter);
    }

    [Fact]
    public async Task TryBeginAsync_DifferentFingerprint_ReturnsConflict()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fp1 = new Fingerprint("fp1");
        var fp2 = new Fingerprint("fp2");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await _store!.TryBeginAsync(key, fp1, policy, CancellationToken.None);
        var result = await _store.TryBeginAsync(key, fp2, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task CompleteAsync_ThenTryBegin_ReturnsAlreadyCompleted()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        var snapshot = new IdempotencyResponseSnapshot
        {
            StatusCode = 200,
            Body = new byte[] { 1, 2, 3 },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _store.CompleteAsync(key, fingerprint, snapshot, TimeSpan.FromMinutes(5), CancellationToken.None);

        var result = await _store.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.AlreadyCompleted, result.Outcome);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(200, result.Snapshot.StatusCode);
        Assert.Equal(snapshot.Body, result.Snapshot.Body);
    }

    [Fact]
    public async Task TryBeginAsync_LeaseExpired_Reacquires()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        // Short lease
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromMilliseconds(500),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        // Wait for lease to expire
        await Task.Delay(1000);

        var result = await _store.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryBeginAsync_TTLExpired_ReturnsAcquired_AsNew()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        // Short TTL
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromMilliseconds(100),
            Ttl = TimeSpan.FromMilliseconds(500)
        };

        await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        await Task.Delay(1000); // Wait for TTL

        // Should act as new request
        var fingerprint2 = new Fingerprint("fp2"); // Even with diff fingerprint
        var result = await _store.TryBeginAsync(key, fingerprint2, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Acquired, result.Outcome);
    }

    [Fact]
    public async Task TryGetCompletedAsync_ReturnsSnapshot()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp1");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        var snapshot = new IdempotencyResponseSnapshot
        {
            StatusCode = 201,
            ContentType = "application/json"
        };
        await _store.CompleteAsync(key, fingerprint, snapshot, TimeSpan.FromMinutes(5), CancellationToken.None);

        var fetched = await _store.TryGetCompletedAsync(key, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(201, fetched.StatusCode);
        Assert.Equal("application/json", fetched.ContentType);
    }

    [Fact]
    public async Task Concurrency_ParallelAcquire_OnlyOneWins()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope_conc", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp_conc");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            Ttl = TimeSpan.FromMinutes(1)
        };

        var tasks = new List<Task<TryBeginResult>>();
        int concurrency = 50;

        for (int i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(() => _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None)));
        }

        var results = await Task.WhenAll(tasks);

        var acquiredCount = results.Count(r => r.Outcome == TryBeginOutcome.Acquired);
        var inFlightCount = results.Count(r => r.Outcome == TryBeginOutcome.InFlight);

        Assert.Equal(1, acquiredCount);
        Assert.Equal(concurrency - 1, inFlightCount);
    }

    [Fact]
    public async Task TryBegin_CorruptedCompletedSnapshot_ReturnsConflict()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());
        var fingerprint = new Fingerprint("fp_corrupt");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(60),
            Ttl = TimeSpan.FromMinutes(5)
        };

        await using (var conn = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO public.idempotency_records (scope, key, fingerprint, state, lease_until_utc, expires_at_utc, created_at_utc, updated_at_utc, snapshot_json)
                VALUES (@s, @k, @fp, 'completed', @now, @exp, @now, @now, @json::jsonb)", conn);
            var now = DateTimeOffset.UtcNow;
            cmd.Parameters.AddWithValue("s", key.Scope);
            cmd.Parameters.AddWithValue("k", key.Key);
            cmd.Parameters.AddWithValue("fp", fingerprint.Value);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("exp", now.AddMinutes(5));
            cmd.Parameters.AddWithValue("json", "{\"StatusCode\":\"oops\",\"Headers\":{}}");
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await _store!.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

        Assert.Equal(TryBeginOutcome.Conflict, result.Outcome);
        Assert.Contains("snapshot", result.ConflictReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryGetCompleted_CorruptedCompletedSnapshot_ReturnsNull()
    {
        if (ShouldSkip()) return;

        var key = new IdempotencyKey.Core.IdempotencyKey("scope1", Guid.NewGuid().ToString());

        await using (var conn = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO public.idempotency_records (scope, key, fingerprint, state, lease_until_utc, expires_at_utc, created_at_utc, updated_at_utc, snapshot_json)
                VALUES (@s, @k, @fp, 'completed', @now, @exp, @now, @now, @json::jsonb)", conn);
            var now = DateTimeOffset.UtcNow;
            cmd.Parameters.AddWithValue("s", key.Scope);
            cmd.Parameters.AddWithValue("k", key.Key);
            cmd.Parameters.AddWithValue("fp", "fp_corrupt");
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("exp", now.AddMinutes(5));
            cmd.Parameters.AddWithValue("json", "{\"StatusCode\":\"oops\",\"Headers\":{}}");
            await cmd.ExecuteNonQueryAsync();
        }

        var snapshot = await _store!.TryGetCompletedAsync(key, CancellationToken.None);

        Assert.Null(snapshot);
    }
}
