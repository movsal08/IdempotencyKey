using System.Text.Json.Serialization;
using IdempotencyKey.AspNetCore;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddIdempotencyKey();
builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();

var app = builder.Build();

// app.UseIdempotencyKey(); // Optional if using RequireIdempotency everywhere

app.MapGet("/", () => "Hello World!");

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
