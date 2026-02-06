# IdempotencyKey

A small, AOT-friendly library that provides idempotency for “write” endpoints (POST/PATCH/PUT/DELETE) by storing: idempotency key, request fingerprint (hash), response snapshot, and TTL.

## Goals

- Provide Stripe-like idempotency semantics for ASP.NET Core Minimal APIs.
- Native AOT support.
- Storage agnostic (Memory, Redis, Postgres).

## Core Concepts

The core library (`IdempotencyKey.Core`) defines the state machine and contracts for idempotency.

- **Idempotency Key**: A unique string provided by the client (usually via `Idempotency-Key` header) to identify a specific operation.
- **Scope**: A partitioning key (e.g., Tenant ID, User ID) to prevent collisions across different tenants.
- **Fingerprint**: A deterministic hash of the request (Method, Route, Scope, Selected Headers, Body) used to detect if a key is being reused for a *different* request (Conflict).
- **Lease**: When a request starts, it acquires a "lock" (InFlight state) with a short lease. If the process crashes, the lease expires, allowing retries to re-acquire the lock.
- **TTL**: All idempotency records have a Time-To-Live. After the TTL expires, the key is pruned, and subsequent requests with the same key are treated as new.

## Storage Providers

### Memory
Available in `IdempotencyKey.Store.Memory`.
Suitable for development and testing. Data is lost on process restart.

### Redis
Available in `IdempotencyKey.Store.Redis`.
Production-grade distributed storage using Redis.

```csharp
// Example registration
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
    new RedisIdempotencyStore(new RedisIdempotencyStoreOptions
    {
        Configuration = "localhost:6379",
        KeyPrefix = "idem:"
    }));
```

## Quickstart

(Coming soon)
