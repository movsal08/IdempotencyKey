using IdempotencyKey.AspNetCore;
using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Add Idempotency
builder.Services.AddIdempotencyKey();
builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();

var app = builder.Build();

app.UseRouting();

// REQUIRED: Enable Idempotency Middleware to handle attributes and buffering
app.UseIdempotencyKey();

app.MapControllers();

app.MapGet("/", () => "MVC Controller Sample Running");

app.Run();
