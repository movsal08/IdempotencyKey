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
        var attribute = endpoint?.Metadata.GetMetadata<RequireIdempotencyAttribute>();

        // If attribute exists and no explicit metadata (from Minimal API extensions), use attribute
        if (metadata == null && attribute != null)
        {
            metadata = attribute.ToMetadata();
        }

        bool shouldRun = false;

        if (metadata != null)
        {
            // Explicit opt-in via metadata
            // Ensure buffering is enabled even if enforced by filter, because filter runs after model binding
            context.Request.EnableBuffering();

            // If enforced by filter, middleware should skip to avoid double execution
            if (!metadata.EnforcedByFilter)
            {
                shouldRun = true;
            }
        }
        else if (options.Predicate(context))
        {
            context.Request.EnableBuffering();
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
