using IdempotencyKey.Core;
using IdempotencyKey.Store.Memory;
using IdempotencyKeyType = IdempotencyKey.Core.IdempotencyKey;

namespace Benchmarks;

public static class ConcurrencySmokeTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("Running Concurrency Smoke Test (100 parallel requests)...");

        using var store = new MemoryIdempotencyStore();
        var key = new IdempotencyKeyType("smoke", "key-" + Guid.NewGuid());
        var fingerprint = new Fingerprint("hash-smoke");
        var policy = new IdempotencyPolicy
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            Ttl = TimeSpan.FromMinutes(1)
        };

        int acquiredCount = 0;
        int inFlightCount = 0;
        int completedCount = 0;
        int conflictCount = 0;
        int errorCount = 0;

        var tasks = new List<Task>();
        var startSignal = new TaskCompletionSource();

        // Launch 100 tasks
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Wait for signal to start all at once
                await startSignal.Task;
                try
                {
                    var result = await store.TryBeginAsync(key, fingerprint, policy, CancellationToken.None);

                    if (result.Outcome == TryBeginOutcome.Acquired)
                    {
                        Interlocked.Increment(ref acquiredCount);
                        // Simulate work (e.g. 50ms)
                        await Task.Delay(50);

                        // Complete
                        var snapshot = new IdempotencyResponseSnapshot
                        {
                            StatusCode = 200,
                            Body = new byte[] { 1, 2, 3 },
                            ContentType = "application/json"
                        };
                        await store.CompleteAsync(key, fingerprint, snapshot, policy.Ttl, CancellationToken.None);
                    }
                    else if (result.Outcome == TryBeginOutcome.InFlight)
                    {
                        Interlocked.Increment(ref inFlightCount);
                    }
                    else if (result.Outcome == TryBeginOutcome.AlreadyCompleted)
                    {
                        Interlocked.Increment(ref completedCount);
                    }
                    else if (result.Outcome == TryBeginOutcome.Conflict)
                    {
                         Interlocked.Increment(ref conflictCount);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errorCount);
                    Console.WriteLine($"Error: {ex}");
                }
            }));
        }

        // Start!
        startSignal.SetResult();
        await Task.WhenAll(tasks);

        Console.WriteLine($"Results:");
        Console.WriteLine($"  Acquired:  {acquiredCount}");
        Console.WriteLine($"  InFlight:  {inFlightCount}");
        Console.WriteLine($"  Completed: {completedCount}");
        Console.WriteLine($"  Conflict:  {conflictCount}");
        Console.WriteLine($"  Errors:    {errorCount}");

        if (acquiredCount != 1)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILURE: Expected exactly 1 Acquired, but got {acquiredCount}.");
            Console.ResetColor();
            Environment.Exit(1);
        }

        if (errorCount > 0)
        {
             Console.ForegroundColor = ConsoleColor.Red;
             Console.WriteLine("FAILURE: Errors occurred.");
             Console.ResetColor();
             Environment.Exit(1);
        }

        if (conflictCount > 0)
        {
             Console.ForegroundColor = ConsoleColor.Red;
             Console.WriteLine("FAILURE: Unexpected conflicts.");
             Console.ResetColor();
             Environment.Exit(1);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("SUCCESS: Exactly one execution occurred.");
        Console.ResetColor();
    }
}
