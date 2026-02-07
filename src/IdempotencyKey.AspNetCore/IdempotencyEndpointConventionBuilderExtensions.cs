using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKey.AspNetCore;

public static class IdempotencyEndpointConventionBuilderExtensions
{
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder, Action<IdempotencyPolicyMetadata>? configure = null)
        where TBuilder : IEndpointConventionBuilder
    {
        var metadata = new IdempotencyPolicyMetadata { EnforcedByFilter = true };
        configure?.Invoke(metadata);

        builder.Add(endpointBuilder =>
        {
            endpointBuilder.Metadata.Add(metadata);
        });

        builder.AddEndpointFilterFactory((context, next) =>
        {
            var filter = new IdempotencyEndpointFilter(metadata);
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });

        return builder;
    }

    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder, Action<IdempotencyPolicyMetadata>? configure = null)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.RequireIdempotency(configure);
    }
}
