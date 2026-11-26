namespace ReeveWorkflow.Core.Hosting;

internal sealed class WorkflowStartupChecks
{
    private readonly Dictionary<string, Action<IServiceProvider>> checks = [];

    internal void Add(string key, Action<IServiceProvider> check)
    {
        lock (checks)
            checks.TryAdd(key, check);
    }

    internal void Run(IServiceProvider services)
    {
        Action<IServiceProvider>[] snapshot;
        lock (checks)
            snapshot = [.. checks.Values];

        foreach (var check in snapshot)
            check(services);
    }
}
