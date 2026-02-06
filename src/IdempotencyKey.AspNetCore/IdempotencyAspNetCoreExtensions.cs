using IdempotencyKey.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyKey.AspNetCore;

public static class IdempotencyAspNetCoreExtensions
{
    public static IServiceCollection AddIdempotencyKey(this IServiceCollection services, Action<IdempotencyAspNetCoreOptions>? configureOptions = null)
    {
        services.AddOptions<IdempotencyAspNetCoreOptions>();
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<IFingerprintProvider, Sha256FingerprintProvider>();
        services.AddSingleton<IRequestBodyHasher, DefaultRequestBodyHasher>();
        services.AddScoped<IdempotencyService>();

        return services;
    }
}
