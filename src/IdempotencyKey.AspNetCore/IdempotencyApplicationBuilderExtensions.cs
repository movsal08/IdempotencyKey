using Microsoft.AspNetCore.Builder;

namespace IdempotencyKey.AspNetCore;

public static class IdempotencyApplicationBuilderExtensions
{
    public static IApplicationBuilder UseIdempotencyKey(this IApplicationBuilder app)
    {
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
