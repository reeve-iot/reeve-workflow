using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

internal sealed class WorkflowResumedFaultException(string code, string? category, string message)
    : Exception(message), IWorkflowCodedException
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code) ? WorkflowErrorCodes.BlockFailed : code;

    public string? Category { get; } = category;
}

internal sealed class WorkflowHostLookupException(Exception inner)
    : Exception("A host lookup failed while the resumed node's outputs were prepared.", inner);

internal static class WorkflowResumedFailure
{
    private static readonly AsyncLocal<Armed?> current = new();

    public static void Swap(Armed? armed) => current.Value = armed;

    public static Exception? TakeFor(Guid executionId, string nodeId) =>
        current.Value is { } armed && armed.ExecutionId == executionId && armed.NodeId == nodeId && armed.Take()
            ? armed.Failure
            : null;

    internal sealed class Armed(Guid executionId, string nodeId, Exception failure)
    {
        private int taken;

        public Guid ExecutionId { get; } = executionId;
        public string NodeId { get; } = nodeId;
        public Exception Failure { get; } = failure;

        public bool Take() => Interlocked.Exchange(ref taken, 1) == 0;
    }
}
