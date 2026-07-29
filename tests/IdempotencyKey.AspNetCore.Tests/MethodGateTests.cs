using System.Net;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyKey.AspNetCore.Tests;

/// <summary>
/// Idempotency must only apply to state-changing methods, even when an endpoint (or a whole
/// controller / route group) opted in wholesale.
/// </summary>
public class MethodGateTests
{
    private static async Task<IHost> CreateHost(
        Action<IServiceCollection>? configureServices = null,
        Action<IApplicationBuilder>? configureApp = null)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddIdempotencyKey();
                    services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
                    configureServices?.Invoke(services);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    configureApp?.Invoke(app);
                });
            });

        return await builder.StartAsync();
    }

    [Fact]
    public async Task Get_OnOptedInEndpoint_DoesNotRequireKey()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", () => "ok").RequireIdempotency();
            });
        });

        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_OnOptedInEndpoint_IsNeverReplayed()
    {
        var executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", () =>
                {
                    Interlocked.Increment(ref executionCount);
                    return "ok";
                }).RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        await client.GetAsync("/");
        await client.GetAsync("/");

        Assert.Equal(2, executionCount);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task StateChangingMethods_StillRequireKey(string method)
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMethods("/", new[] { method }, () => "ok").RequireIdempotency();
            });
        });

        var response = await host.GetTestClient()
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), "/"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApplicableMethods_CanBeNarrowedGlobally()
    {
        using var host = await CreateHost(
            configureServices: services => services.Configure<IdempotencyAspNetCoreOptions>(o =>
            {
                o.ApplicableMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POST" };
            }),
            configureApp: app =>
            {
                app.UseIdempotencyKey();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapPut("/", () => "ok").RequireIdempotency();
                });
            });

        var response = await host.GetTestClient().PutAsync("/", new StringContent("test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PerEndpointMethods_OverrideTheGlobalSet()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", () => "ok")
                    .RequireIdempotency(opt => opt.Methods = new[] { "GET" });
            });
        });

        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AttributeOnGetAction_DoesNotRequireKey()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", () => "ok")
                    .WithMetadata(new RequireIdempotencyAttribute { TtlSeconds = 60 });
            });
        });

        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AttributeOnPostAction_StillRequiresKey()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", () => "ok")
                    .WithMetadata(new RequireIdempotencyAttribute { TtlSeconds = 60 });
            });
        });

        var response = await host.GetTestClient().PostAsync("/", new StringContent("test"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
