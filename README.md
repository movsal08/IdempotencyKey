# IdempotencyKey.AspNetCore

A robust, AOT-friendly library that adds Stripe-like idempotency semantics to ASP.NET Core endpoints. It ensures that retrying a request with the same `Idempotency-Key` results in the same response, while rejecting reuse of the key for different requests.

## Features

- **Stripe-like Semantics**:
  - **Replay**: Same Key + Same Request Fingerprint → Returns stored response (status + headers + body).
  - **Conflict**: Same Key + Different Request Fingerprint → Returns `409 Conflict`.
  - **In-Flight Handling**: Concurrent requests with same key wait for the first one to complete (or return 409/429 based on config).
- **Native AOT Compatible**: Designed for Minimal APIs and uses `System.Text.Json` source generation.
- **Storage Agnostic**: Providers for Memory, Redis, and Postgres.
- **Flexible Integration**: Supports Minimal APIs (Filter) and Controllers (Attribute/Middleware).

## Installation

(NuGet package coming soon)

For now, reference the project or copy the source.

## Usage

### 1. Minimal API (Per-Endpoint)

The most granular way to use idempotency is via the `.RequireIdempotency()` extension method on Minimal API endpoints.

**Important:** If your endpoint uses Model Binding (e.g. `[FromBody]`, or typed arguments like `MyRequest req`), you **MUST** enable buffering middleware globally to allow the idempotency filter to read the body.

```csharp
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddIdempotencyKey(); // Registers services
builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>(); // Choose a store

var app = builder.Build();

// REQUIRED if using Model Binding (e.g., Post/Put with body)
app.UseIdempotencyKey();

app.MapPost("/payments", (PaymentRequest req) =>
{
    // Process payment...
    return Results.Ok(new { Status = "Processed", Id = Guid.NewGuid() });
})
.RequireIdempotency(options =>
{
    options.Ttl = TimeSpan.FromMinutes(60);
});

app.Run();
```

### 2. Minimal API (Group)

You can apply idempotency to an entire route group.

```csharp
var payments = app.MapGroup("/payments")
    .RequireIdempotency(); // Applies to all endpoints in group

payments.MapPost("/", ...); // Idempotent
payments.MapPost("/{id}/refund", ...); // Idempotent
```

### 3. Controllers (Attribute)

For MVC Controllers, use the `[RequireIdempotency]` attribute. You must also register the middleware.

```csharp
// Program.cs
builder.Services.AddControllers();
builder.Services.AddIdempotencyKey();
// ... register store ...

var app = builder.Build();
app.UseRouting();

// REQUIRED: Middleware handles the attribute logic
app.UseIdempotencyKey();

app.MapControllers();
app.Run();
```

```csharp
// PaymentsController.cs
[ApiController]
[Route("[controller]")]
public class PaymentsController : ControllerBase
{
    [HttpPost]
    [RequireIdempotency(TtlSeconds = 60)]
    public IActionResult Create([FromBody] PaymentRequest request)
    {
        return Ok(new { Status = "Processed" });
    }
}
```

## Concepts

### Idempotency Key
Clients must send a unique key in the `Idempotency-Key` header (configurable). If the header is missing on a required endpoint, the server returns `400 Bad Request`.

### Fingerprint & Conflict
The library computes a SHA-256 fingerprint of the request including:
- HTTP Method
- Route Template
- Request Body (raw bytes)
- Selected Headers (configurable)

If a client sends the same Key but a **different** body/fingerprint, the server returns `409 Conflict`. This prevents accidental misuse of keys.

### In-Flight Requests
If two requests with the same key arrive concurrently:
1. The first acquires the lock (In-Flight).
2. The second waits (default 10s) for the first to complete.
   - If first completes: Second gets the replayed response.
   - If timeout: Second gets `409 Conflict` (or `429` / `Retry-After` depending on config).

### Storage Providers

#### Memory
Good for testing/dev.
```csharp
builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
```

#### Redis
Production-grade distributed store.
```csharp
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
    new RedisIdempotencyStore(new RedisIdempotencyStoreOptions { Configuration = "localhost:6379" }));
```

#### Postgres
Transactional storage using `INSERT ... ON CONFLICT`.
```csharp
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
    new PostgresIdempotencyStore(new PostgresIdempotencyStoreOptions { ConnectionString = "..." }));
```

## Benchmarks

The solution includes a BenchmarkDotNet project to measure performance of core components.

### Running Benchmarks
To run the benchmarks, use the following command:
```bash
dotnet run -c Release --project benchmarks/IdempotencyKey.Benchmarks
```

This will run benchmarks for:
- **Fingerprinting**: Hashing request bodies of various sizes (1KB, 16KB, 256KB) and computing the full request fingerprint.
- **Store Operations**: Performance of `TryBeginAsync` (Acquire/Replay) and `CompleteAsync` using the `MemoryIdempotencyStore`.

### Concurrency Smoke Test
To verify concurrency correctness (ensure exactly-once execution under load), run the lightweight smoke test:
```bash
dotnet run -c Release --project benchmarks/IdempotencyKey.Benchmarks -- smoke
```
This fires 100 parallel requests with the same key and asserts that only one is executed.

## Performance Notes

- **Max Snapshot Size**: The library limits the size of the response body stored in the snapshot to avoid bloating the storage provider. The default is **256 KB**. Responses larger than this will result in an error (or truncated/placeholder behavior depending on configuration).
- **Body Hashing**: The default hasher computes SHA-256 over the entire request body to ensure collision resistance. There is currently no upper limit on the request body size for hashing, so extremely large bodies will impact CPU/latency. Use typical API payload sizes (e.g., < 1MB) for best performance.

## How to Test

You can verify the behavior using PowerShell or curl.

### PowerShell

```powershell
$headers = @{ 'Idempotency-Key' = 'test-key-1' }
$body = '{ "amount": 100, "currency": "USD" }'

# First Request -> 200 OK (Executed)
Invoke-RestMethod -Method Post -Uri https://localhost:7146/payments -Headers $headers -ContentType 'application/json' -Body $body

# Second Request (Same Body) -> 200 OK (Replayed, same result)
Invoke-RestMethod -Method Post -Uri https://localhost:7146/payments -Headers $headers -ContentType 'application/json' -Body $body

# Third Request (Different Body) -> 409 Conflict
$body2 = '{ "amount": 200, "currency": "USD" }'
try {
    Invoke-RestMethod -Method Post -Uri https://localhost:7146/payments -Headers $headers -ContentType 'application/json' -Body $body2
} catch {
    Write-Host "Conflict: $($_.Exception.Response.StatusCode)"
}
```

### cURL

```bash
# 1. First Request
curl -v -X POST https://localhost:7146/payments \
  -H "Idempotency-Key: key1" \
  -H "Content-Type: application/json" \
  -d '{"amount": 10}'

# 2. Replay (Same Key, Same Body) -> Should return 200 OK (Cached)
curl -v -X POST https://localhost:7146/payments \
  -H "Idempotency-Key: key1" \
  -H "Content-Type: application/json" \
  -d '{"amount": 10}'

# 3. Conflict (Same Key, Different Body) -> Should return 409 Conflict
curl -v -X POST https://localhost:7146/payments \
  -H "Idempotency-Key: key1" \
  -H "Content-Type: application/json" \
  -d '{"amount": 20}'
```
