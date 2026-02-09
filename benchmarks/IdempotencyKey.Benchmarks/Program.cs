using BenchmarkDotNet.Running;
using Benchmarks;

if (args.Contains("smoke", StringComparer.OrdinalIgnoreCase))
{
    await ConcurrencySmokeTest.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(FingerprintBenchmarks).Assembly).Run(args);
