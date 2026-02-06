# IdempotencyKey Benchmarks

This project uses [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure performance of hashing operations and storage providers.

## Running benchmarks

Run the benchmarks in Release configuration:

```bash
dotnet run -c Release --project benchmarks/IdempotencyKey.Benchmarks
```

You can select specific benchmarks by passing arguments:

```bash
dotnet run -c Release --project benchmarks/IdempotencyKey.Benchmarks --filter *Hashing*
dotnet run -c Release --project benchmarks/IdempotencyKey.Benchmarks --filter *Store*
```
