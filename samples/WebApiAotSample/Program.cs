using System.Text.Json.Serialization;
using IdempotencyKey.AspNetCore;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddIdempotencyKey();
builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();

var app = builder.Build();

// Ensure buffering is enabled for Minimal APIs with model binding (required for idempotency)
app.UseIdempotencyKey();

app.MapGet("/", () => "Hello World!");

// Help endpoint showing usage examples
app.MapGet("/payments/help", () =>
    "PowerShell Example:\n" +
    "$headers = @{ 'Idempotency-Key' = 'key1' }\n" +
    "Invoke-RestMethod -Method Post -Uri http://localhost:5173/payments -Headers $headers -ContentType 'application/json' -Body '{ \"amount\": 10, \"currency\": \"USD\" }'\n\n" +
    "Replay with same body -> 200 OK (same TransactionId)\n" +
    "Different body -> 409 Conflict\n");

// Debug endpoint to check fingerprint computation (Development only)
if (app.Environment.IsDevelopment())
{
    app.MapMethods("/payments/_debug/fingerprint", new[] { "GET", "POST" }, async (HttpContext context, IdempotencyService service) =>
    {
        var (key, fingerprint, error) = await service.PrepareRequestAsync(context);
        if (error != null) return Results.BadRequest(new { error });

        return Results.Ok(new {
            Key = key?.ToString(),
            Fingerprint = fingerprint?.ToString(),
            Method = context.Request.Method,
            ContentLength = context.Request.ContentLength
        });
    });
}

var payments = app.MapGroup("/payments");
int _paymentCounter = 0;

payments.MapPost("/", (PaymentRequest req) =>
{
    Interlocked.Increment(ref _paymentCounter);
    return new PaymentResponse("Processed", Guid.NewGuid().ToString());
})
.RequireIdempotency(opt =>
{
    opt.Ttl = TimeSpan.FromMinutes(5);
});

payments.MapGet("/count", () => _paymentCounter);

app.Run();

public record PaymentRequest(decimal Amount, string Currency);
public record PaymentResponse(string Status, string TransactionId);

[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
