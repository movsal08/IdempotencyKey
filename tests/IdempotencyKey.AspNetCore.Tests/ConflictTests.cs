using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IdempotencyKey.AspNetCore.Tests;

public record TestRequest(int Amount, string Currency);

public class ConflictTests
{
    private async Task<IHost> CreateHost(
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
    public async Task WithMiddleware_AndModelBinding_ReturnsConflict()
    {
        int executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey(); // Middleware enabled
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", (TestRequest req) =>
                {
                    Interlocked.Increment(ref executionCount);
                    return Results.Ok(new { message = "processed", req });
                }).RequireIdempotency();
            });
        });

        await RunConflictScenario(host, executionCount: 1);
    }

    [Fact]
    public async Task WithoutMiddleware_AndModelBinding_ThrowsException()
    {
        // Without middleware, model binding consumes body before filter runs.
        // The service now detects this and throws InvalidOperationException instead of silently continuing.

        int executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseDeveloperExceptionPage();
            // app.UseIdempotencyKey(); // Middleware OMITTED
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", (TestRequest req) =>
                {
                    Interlocked.Increment(ref executionCount);
                    return Results.Ok(new { message = "processed", req });
                }).RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/");
        request1.Headers.Add("Idempotency-Key", key);
        request1.Content = new StringContent("{\"amount\":10,\"currency\":\"USD\"}", Encoding.UTF8, "application/json");

        // The exception might bubble up (TestServer) or return 500 (Pipeline).
        // We handle both.
        try
        {
             var response = await client.SendAsync(request1);
             Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
             var content = await response.Content.ReadAsStringAsync();
             Assert.Contains("app.UseIdempotencyKey()", content);
        }
        catch (InvalidOperationException ex)
        {
             Assert.Contains("app.UseIdempotencyKey()", ex.Message);
        }
    }

    [Fact]
    public async Task WithoutMiddleware_ManualBodyRead_ReturnsConflict()
    {
        // If we don't use model binding, the body is available for the filter.

        int executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            // app.UseIdempotencyKey(); // Middleware OMITTED
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async (HttpContext ctx) =>
                {
                    // Filter runs first (buffers/reads).
                    // Then we read. But wait, if middleware didn't buffer, Filter did.
                    // Filter calls EnableBuffering. So we can read it here.

                    using var reader = new StreamReader(ctx.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    Interlocked.Increment(ref executionCount);
                    return Results.Ok(new { message = "processed", bodyLength = body.Length });
                }).RequireIdempotency();
            });
        });

        await RunConflictScenario(host, executionCount: 1);
    }

    private async Task RunConflictScenario(IHost host, int executionCount)
    {
        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/");
        request1.Headers.Add("Idempotency-Key", key);
        request1.Content = new StringContent("{\"amount\":10,\"currency\":\"USD\"}", Encoding.UTF8, "application/json");

        var response1 = await client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/");
        request2.Headers.Add("Idempotency-Key", key);
        request2.Content = new StringContent("{\"amount\":20,\"currency\":\"EUR\"}", Encoding.UTF8, "application/json");

        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
    }
}
