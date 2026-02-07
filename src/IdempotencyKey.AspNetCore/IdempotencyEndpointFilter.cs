using IdempotencyKey.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyEndpointFilter : IEndpointFilter
{
    private readonly IdempotencyPolicyMetadata? _metadata;

    public IdempotencyEndpointFilter(IdempotencyPolicyMetadata? metadata)
    {
        _metadata = metadata;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var service = context.HttpContext.RequestServices.GetRequiredService<IdempotencyService>();
        var settings = service.ResolveSettings(_metadata);

        var (key, fingerprint, error) = await service.PrepareRequestAsync(context.HttpContext);
        if (error != null)
        {
            return Results.BadRequest(error);
        }

        if (key == null || fingerprint == null)
        {
            return Results.BadRequest("Idempotency key error.");
        }

        var result = await service.TryBeginAsync(key.Value, fingerprint.Value, settings, context.HttpContext.RequestAborted);

        switch (result.Outcome)
        {
            case TryBeginOutcome.Acquired:
                var executionResult = await next(context);
                return new IdempotentResult(executionResult, service, key.Value, fingerprint.Value, settings);

            case TryBeginOutcome.AlreadyCompleted:
                return new IdempotentReplayResult(service, result.Snapshot!);

            case TryBeginOutcome.Conflict:
                return new IdempotentConflictResult(service, result.ConflictReason);

            case TryBeginOutcome.InFlight:
                await service.HandleInFlightAsync(context.HttpContext, key.Value, result, settings);
                return Results.Empty;

            default:
                return Results.StatusCode(500);
        }
    }

    private class IdempotentReplayResult : IResult
    {
        private readonly IdempotencyService _service;
        private readonly IdempotencyResponseSnapshot _snapshot;

        public IdempotentReplayResult(IdempotencyService service, IdempotencyResponseSnapshot snapshot)
        {
            _service = service;
            _snapshot = snapshot;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            return _service.ReplaySnapshotAsync(httpContext, _snapshot);
        }
    }

    private class IdempotentConflictResult : IResult
    {
        private readonly IdempotencyService _service;
        private readonly string? _reason;

        public IdempotentConflictResult(IdempotencyService service, string? reason)
        {
            _service = service;
            _reason = reason;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            return _service.HandleConflictAsync(httpContext, _reason);
        }
    }

    private class IdempotentResult : IResult
    {
        private readonly object? _inner;
        private readonly IdempotencyService _service;
        private readonly IdempotencyKey.Core.IdempotencyKey _key;
        private readonly Fingerprint _fingerprint;
        private readonly IdempotencyService.ExecutionSettings _settings;

        public IdempotentResult(
            object? inner,
            IdempotencyService service,
            IdempotencyKey.Core.IdempotencyKey key,
            Fingerprint fingerprint,
            IdempotencyService.ExecutionSettings settings)
        {
            _inner = inner;
            _service = service;
            _key = key;
            _fingerprint = fingerprint;
            _settings = settings;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var originalBody = httpContext.Response.Body;
            using var responseBuffer = new MemoryStream();
            httpContext.Response.Body = responseBuffer;

            try
            {
                if (_inner is IResult r)
                {
                    await r.ExecuteAsync(httpContext);
                }
                else if (_inner is string s)
                {
                    // Simple string result
                    await httpContext.Response.WriteAsync(s);
                }
                else if (_inner != null)
                {
                    // JSON by default for objects
                    await httpContext.Response.WriteAsJsonAsync(_inner);
                }

                httpContext.Response.Body = originalBody;

                await _service.ProcessResponseAndCompleteAsync(httpContext, responseBuffer, _key, _fingerprint, _settings);

                responseBuffer.Position = 0;
                await responseBuffer.CopyToAsync(originalBody);
            }
            catch
            {
                httpContext.Response.Body = originalBody;
                throw;
            }
        }
    }
}
