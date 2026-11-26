namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowHomeTimeZone
{
    internal static ValueTask<TimeZoneInfo> ResolveAsync(
        TimeZoneInfo configured,
        Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? resolve,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        resolve is null ? new(configured) : new(resolve(services, cancellationToken));
}
