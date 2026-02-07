using IdempotencyKey.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IdempotencyKeyStruct = IdempotencyKey.Core.IdempotencyKey;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyService
{
    private readonly IIdempotencyStore _store;
    private readonly IFingerprintProvider _fingerprintProvider;
    private readonly IRequestBodyHasher _hasher;
    private readonly IdempotencyAspNetCoreOptions _options;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(
        IIdempotencyStore store,
        IFingerprintProvider fingerprintProvider,
        IRequestBodyHasher hasher,
        IOptions<IdempotencyAspNetCoreOptions> options,
        ILogger<IdempotencyService> logger)
    {
        _store = store;
        _fingerprintProvider = fingerprintProvider;
        _hasher = hasher;
        _options = options.Value;
        _logger = logger;
    }

    public record ExecutionSettings(
        IdempotencyPolicy Policy,
        InFlightMode InFlightMode,
        TimeSpan WaitTimeout,
        double RetryAfterSeconds,
        int MaxSnapshotBytes
    );

    public async Task ExecuteAsync(HttpContext httpContext, RequestDelegate next, IdempotencyPolicyMetadata? metadata)
    {
        var settings = ResolveSettings(metadata);

        var (key, fingerprint, validationError) = await PrepareRequestAsync(httpContext);
        if (validationError != null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync(validationError);
            return;
        }

        if (key == null || fingerprint == null)
        {
            // Should be covered by validationError check, but compiler doesn't know.
            return;
        }

        // 4. TryBegin
        var result = await _store.TryBeginAsync(key.Value, fingerprint.Value, settings.Policy, httpContext.RequestAborted);

        switch (result.Outcome)
        {
            case TryBeginOutcome.Acquired:
                await HandleAcquiredAsync(httpContext, next, key.Value, fingerprint.Value, settings);
                break;
            case TryBeginOutcome.AlreadyCompleted:
                await ReplaySnapshotAsync(httpContext, result.Snapshot!);
                break;
            case TryBeginOutcome.Conflict:
                await HandleConflictAsync(httpContext, result.ConflictReason);
                break;
            case TryBeginOutcome.InFlight:
                await HandleInFlightAsync(httpContext, key.Value, result, settings);
                break;
        }
    }

    // Exposed for Filter
    public async Task<(IdempotencyKeyStruct? Key, Fingerprint? Fingerprint, string? Error)> PrepareRequestAsync(HttpContext httpContext)
    {
        // 1. Validate Header
        if (!httpContext.Request.Headers.TryGetValue(_options.HeaderName, out var headerValue) || string.IsNullOrWhiteSpace(headerValue))
        {
            return (null, null, $"Missing or empty {_options.HeaderName} header.");
        }

        var keyString = headerValue.ToString();
        var scope = _options.ScopeProvider(httpContext);
        var key = new IdempotencyKeyStruct(scope, keyString);

        // 3. Compute Fingerprint
        httpContext.Request.EnableBuffering();

        byte[] bodyHash = Array.Empty<byte>();

        if (httpContext.Request.ContentLength > 0 || (httpContext.Request.ContentLength == null && httpContext.Request.Body.CanSeek))
        {
            httpContext.Request.Body.Position = 0;
            using var memoryStream = new MemoryStream();
            await httpContext.Request.Body.CopyToAsync(memoryStream, httpContext.RequestAborted);
            bodyHash = _hasher.Hash(memoryStream.ToArray());
            httpContext.Request.Body.Position = 0;
        }

        var selectedHeaders = new Dictionary<string, string[]>();
        foreach (var h in _options.HeaderKeysToIncludeInFingerprint)
        {
            if (httpContext.Request.Headers.TryGetValue(h, out var val))
            {
                selectedHeaders[h] = val.Where(x => x != null).Select(x => x!).ToArray();
            }
        }

        var endpoint = httpContext.GetEndpoint();
        var routeTemplate = (endpoint as RouteEndpoint)?.RoutePattern?.RawText ?? httpContext.Request.Path.Value ?? "";

        var fingerprint = _fingerprintProvider.Compute(
            httpContext.Request.Method,
            routeTemplate,
            scope,
            selectedHeaders,
            bodyHash);

        return (key, fingerprint, null);
    }

    public async Task<TryBeginResult> TryBeginAsync(IdempotencyKeyStruct key, Fingerprint fingerprint, ExecutionSettings settings, CancellationToken ct)
    {
        return await _store.TryBeginAsync(key, fingerprint, settings.Policy, ct);
    }

    private async Task HandleAcquiredAsync(HttpContext httpContext, RequestDelegate next, IdempotencyKeyStruct key, Fingerprint fingerprint, ExecutionSettings settings)
    {
        var originalBodyStream = httpContext.Response.Body;
        using var responseBuffer = new MemoryStream();
        httpContext.Response.Body = responseBuffer;

        try
        {
            await next(httpContext);

            httpContext.Response.Body = originalBodyStream;

            await ProcessResponseAndCompleteAsync(httpContext, responseBuffer, key, fingerprint, settings);

            // Copy to original stream
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalBodyStream);
        }
        catch (Exception)
        {
            httpContext.Response.Body = originalBodyStream;
            throw;
        }
    }

    // Exposed for Filter (via wrapping)
    public async Task ProcessResponseAndCompleteAsync(HttpContext httpContext, MemoryStream responseBuffer, IdempotencyKeyStruct key, Fingerprint fingerprint, ExecutionSettings settings)
    {
        // Check limits
        if (responseBuffer.Length > settings.MaxSnapshotBytes)
        {
            _logger.LogWarning("Response size {Size} exceeded max snapshot size {Max}. Returning 413.", responseBuffer.Length, settings.MaxSnapshotBytes);

            httpContext.Response.Clear();
            httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

            var errorSnapshot = new IdempotencyResponseSnapshot
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
                ContentType = "text/plain",
                Body = System.Text.Encoding.UTF8.GetBytes("Response exceeded idempotency snapshot limit."),
                Headers = new Dictionary<string, string[]>()
            };

            await _store.CompleteAsync(key, fingerprint, errorSnapshot, settings.Policy.Ttl, httpContext.RequestAborted);

            // Write 413 to buffer so it can be copied back
            responseBuffer.SetLength(0); // Clear buffer
            if (errorSnapshot.Body != null)
                await responseBuffer.WriteAsync(errorSnapshot.Body);

            return;
        }

        // Create Snapshot
        var snapshot = new IdempotencyResponseSnapshot
        {
            StatusCode = httpContext.Response.StatusCode,
            ContentType = httpContext.Response.ContentType,
            Body = responseBuffer.ToArray(),
            Headers = new Dictionary<string, string[]>()
        };

        foreach (var h in httpContext.Response.Headers)
        {
            if (!IsUnsafeHeader(h.Key))
            {
                snapshot.Headers[h.Key] = h.Value.Where(x => x != null).Select(x => x!).ToArray();
            }
        }

        await _store.CompleteAsync(key, fingerprint, snapshot, settings.Policy.Ttl, httpContext.RequestAborted);
    }

    public async Task HandleInFlightAsync(HttpContext httpContext, IdempotencyKeyStruct key, TryBeginResult result, ExecutionSettings settings)
    {
        if (settings.InFlightMode == InFlightMode.RetryAfter)
        {
            // Immediate return
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            httpContext.Response.Headers["Retry-After"] = ((int)settings.RetryAfterSeconds).ToString();
            await httpContext.Response.WriteAsync("Request is currently in-flight. Please try again later.");
            return;
        }

        // Wait Mode
        var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        cts.CancelAfter(settings.WaitTimeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Poll
                var snapshot = await _store.TryGetCompletedAsync(key, cts.Token);
                if (snapshot != null)
                {
                    await ReplaySnapshotAsync(httpContext, snapshot);
                    return;
                }

                // Wait a bit
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or RequestAborted
            if (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
        }

        // Timeout reached
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        httpContext.Response.Headers["Retry-After"] = ((int)settings.RetryAfterSeconds).ToString();
        await httpContext.Response.WriteAsync("Request timed out while waiting for in-flight operation.");
    }

    public async Task ReplaySnapshotAsync(HttpContext httpContext, IdempotencyResponseSnapshot snapshot)
    {
        httpContext.Response.StatusCode = snapshot.StatusCode;
        if (!string.IsNullOrEmpty(snapshot.ContentType))
        {
            httpContext.Response.ContentType = snapshot.ContentType;
        }

        foreach (var h in snapshot.Headers)
        {
            httpContext.Response.Headers[h.Key] = h.Value;
        }

        if (snapshot.Body != null && snapshot.Body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(snapshot.Body);
        }
    }

    public async Task HandleConflictAsync(HttpContext httpContext, string? reason)
    {
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        // Do not echo sensitive body implies generic message or structured error.
        await httpContext.Response.WriteAsJsonAsync(new { type = "https://tools.ietf.org/html/rfc7231#section-6.5.8", title = "Idempotency Conflict", detail = reason ?? "Idempotency key mismatch." });
    }

    public ExecutionSettings ResolveSettings(IdempotencyPolicyMetadata? metadata)
    {
        var policy = new IdempotencyPolicy
        {
            Ttl = metadata?.Ttl ?? _options.DefaultTtl,
            LeaseDuration = metadata?.LeaseDuration ?? _options.DefaultLease
        };
        policy.Validate();

        return new ExecutionSettings(
            policy,
            metadata?.InFlightMode ?? InFlightMode.Wait,
            metadata?.WaitTimeout ?? _options.DefaultWaitTimeout,
            metadata?.RetryAfterSeconds ?? _options.DefaultRetryAfterSeconds,
            metadata?.MaxSnapshotBytes ?? _options.DefaultMaxSnapshotBytes
        );
    }

    private static bool IsUnsafeHeader(string key)
    {
        var k = key.ToLowerInvariant();
        return k == "date" || k == "server" || k == "transfer-encoding" || k == "connection" || k == "content-length";
    }
}
