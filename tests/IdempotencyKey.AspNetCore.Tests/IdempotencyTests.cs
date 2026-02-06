using System.Net;
using System.Text;
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

public class IdempotencyTests
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
    public async Task Middleware_MissingHeader_Returns400()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", () => "ok").RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        var response = await client.PostAsync("/", new StringContent("test"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Middleware_WithHeader_AcquiresAndReplays()
    {
        int executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async () =>
                {
                    Interlocked.Increment(ref executionCount);
                    return "executed";
                }).RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // First Request
        var response1 = await client.PostAsync("/", new StringContent("test"));
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal("executed", await response1.Content.ReadAsStringAsync());
        Assert.Equal(1, executionCount);

        // Second Request (Replay)
        var response2 = await client.PostAsync("/", new StringContent("test"));
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("executed", await response2.Content.ReadAsStringAsync());
        Assert.Equal(1, executionCount); // Should not increment
    }

    [Fact]
    public async Task Middleware_Conflict_Returns409()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", () => "executed").RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // First Request
        await client.PostAsync("/", new StringContent("body1"));

        // Second Request (Different Body)
        var response2 = await client.PostAsync("/", new StringContent("body2"));
        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
    }

    [Fact]
    public async Task InFlight_WaitMode_WaitsAndReplays()
    {
        var tcs = new TaskCompletionSource();
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async () =>
                {
                    await tcs.Task; // Wait until signal
                    return "executed";
                }).RequireIdempotency(opt =>
                {
                    opt.InFlightMode = InFlightMode.Wait;
                    opt.WaitTimeout = TimeSpan.FromSeconds(5);
                });
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();

        // Start Request 1 (Async)
        var task1 = Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/");
            req.Headers.Add("Idempotency-Key", key);
            req.Content = new StringContent("test");
            return await client.SendAsync(req);
        });

        // Ensure Request 1 has acquired the lock (simple delay)
        await Task.Delay(500);

        // Start Request 2 (Should Wait)
        var task2 = Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/");
            req.Headers.Add("Idempotency-Key", key);
            req.Content = new StringContent("test");
            return await client.SendAsync(req);
        });

        // Release Request 1
        tcs.SetResult();

        var response1 = await task1;
        var response2 = await task2;

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("executed", await response2.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InFlight_RetryAfterMode_Returns409()
    {
        var tcs = new TaskCompletionSource();
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async () =>
                {
                    await tcs.Task;
                    return "executed";
                }).RequireIdempotency(opt =>
                {
                    opt.InFlightMode = InFlightMode.RetryAfter;
                });
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();

        var task1 = Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/");
            req.Headers.Add("Idempotency-Key", key);
            req.Content = new StringContent("test");
            return await client.SendAsync(req);
        });

        await Task.Delay(500);

        var req2 = new HttpRequestMessage(HttpMethod.Post, "/");
        req2.Headers.Add("Idempotency-Key", key);
        req2.Content = new StringContent("test");
        var response2 = await client.SendAsync(req2);

        // Release 1
        tcs.SetResult();
        await task1;

        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
        Assert.True(response2.Headers.RetryAfter != null);
    }

    [Fact]
    public async Task FilterOnly_WithoutMiddleware_Works()
    {
        int executionCount = 0;
        using var host = await CreateHost(configureApp: app =>
        {
            // app.UseIdempotencyKey(); // Intentionally omitted
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async () =>
                {
                    Interlocked.Increment(ref executionCount);
                    return "executed";
                }).RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var response1 = await client.PostAsync("/", new StringContent("test"));
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(1, executionCount);

        var response2 = await client.PostAsync("/", new StringContent("test"));
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task LargeResponse_Returns413()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", () => new string('a', 2000))
                    .RequireIdempotency(opt => opt.MaxSnapshotBytes = 100);
            });
        });

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsync("/", new StringContent("test"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RequestBody_IsReadableByEndpoint()
    {
        using var host = await CreateHost(configureApp: app =>
        {
            app.UseIdempotencyKey();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/echo", async (HttpContext ctx) =>
                {
                    using var reader = new StreamReader(ctx.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    return body;
                }).RequireIdempotency();
            });
        });

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var content = "test-body-content";
        var response = await client.PostAsync("/echo", new StringContent(content));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsStringAsync());
    }
}
