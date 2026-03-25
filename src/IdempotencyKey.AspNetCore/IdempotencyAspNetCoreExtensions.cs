using IdempotencyKey.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        services.AddSingleton<IRequestBodyHasher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<IdempotencyAspNetCoreOptions>>().Value;
            return new DefaultRequestBodyHasher(options.MaxRequestBodyHashBytes);
        });
        services.AddScoped<IdempotencyService>();

        return services;
    }
}
