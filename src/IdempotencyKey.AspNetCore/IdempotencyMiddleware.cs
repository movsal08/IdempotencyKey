using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdempotencyKey.AspNetCore;

public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<IdempotencyService>();
        var options = context.RequestServices.GetRequiredService<IOptions<IdempotencyAspNetCoreOptions>>().Value;

        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<IdempotencyPolicyMetadata>();

        bool shouldRun = false;

        if (metadata != null)
        {
            // Explicit opt-in via metadata
            // If enforced by filter, middleware should skip to avoid double execution
            if (!metadata.EnforcedByFilter)
            {
                shouldRun = true;
            }
        }
        else if (options.Predicate(context))
        {
            shouldRun = true;
        }

        if (shouldRun)
        {
            await service.ExecuteAsync(context, _next, metadata);
        }
        else
        {
            await _next(context);
        }
    }
}
