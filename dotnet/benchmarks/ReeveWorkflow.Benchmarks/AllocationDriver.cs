using System.Diagnostics.Tracing;

namespace ReeveWorkflow.Benchmarks;

internal static class AllocationDriver
{
    public const string Verb = "alloc";

    public static int Run(string[] scenarios)
    {
        var benchmarks = new ExecutorBenchmarks();
        benchmarks.Setup().GetAwaiter().GetResult();
        try
        {
            for (var index = 0; index < scenarios.Length; index++)
            {
                var parts = scenarios[index].Split(':');
                var operation = Operation(benchmarks, parts[0]);
                var iterations = int.Parse(parts[1]);

                for (var round = 0; round < 3; round++)
                {
                    for (var i = 0; i < Math.Max(40, iterations / 10); i++)
                        operation().GetAwaiter().GetResult();
                    Thread.Sleep(300);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                AllocationMarkers.Log.WindowOpened(index);
                for (var i = 0; i < iterations; i++)
                    operation().GetAwaiter().GetResult();
                AllocationMarkers.Log.WindowClosed(index);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Console.WriteLine(
                    $"window {index} scenario {parts[0]}: {iterations} ops, {allocated} B total, {allocated / iterations} B/op");
            }
        }
        finally
        {
            benchmarks.Cleanup();
        }
        return 0;
    }

    private static Func<Task> Operation(ExecutorBenchmarks benchmarks, string scenario) => scenario switch
    {
        "1" => benchmarks.IterateWithConstant,
        "2" => benchmarks.IterateWithExpression,
        "3a" => benchmarks.ChainedNodes,
        "3b" => benchmarks.RelayChainLiterals,
        "3c" => benchmarks.RelayChainWired,
        "4" => benchmarks.WakerPassOverParkedTemplates,
        _ when scenario.StartsWith("loop", StringComparison.Ordinal) => benchmarks.IterateWithConstantOver(int.Parse(scenario[4..])),
        _ => throw new ArgumentException($"Unknown scenario '{scenario}'; expected 1, 2, 3a, 3b, 3c, 4 or loopN.")
    };
}

[EventSource(Name = "ReeveWorkflow-Benchmarks-Allocation")]
internal sealed class AllocationMarkers : EventSource
{
    public static readonly AllocationMarkers Log = new();

    [Event(1)]
    public void WindowOpened(int window) => WriteEvent(1, window);

    [Event(2)]
    public void WindowClosed(int window) => WriteEvent(2, window);
}
