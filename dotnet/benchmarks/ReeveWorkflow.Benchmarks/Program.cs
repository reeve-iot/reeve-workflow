using BenchmarkDotNet.Running;
using ReeveWorkflow.Benchmarks;

if (args.Length > 0 && args[0] == AllocationDriver.Verb)
    return AllocationDriver.Run(args[1..]);

BenchmarkSwitcher.FromTypes([typeof(ExecutorBenchmarks)]).Run(args);
return 0;
