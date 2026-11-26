using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

internal interface IWorkflowRunReentry
{
    Task<WorkflowRunReentryDecision> DecideReentryAsync(
        WorkflowRunStateRow row, WorkflowSuspensionStatePayload checkpoint, CancellationToken cancellationToken);
}

internal readonly record struct WorkflowRunReentryDecision(
    WorkflowRunReentryVerdict Verdict, string? RefusedNodeId, string? InvalidValue = null)
{
    public static WorkflowRunReentryDecision Reenter() => new(WorkflowRunReentryVerdict.Reenter, null);
    public static WorkflowRunReentryDecision Refuse(string nodeId) => new(WorkflowRunReentryVerdict.Refuse, nodeId);
    public static WorkflowRunReentryDecision Replay() => new(WorkflowRunReentryVerdict.Replay, null);

    public static WorkflowRunReentryDecision Abandon(string message) => new(WorkflowRunReentryVerdict.Abandon, null, message);
}

internal enum WorkflowRunReentryVerdict
{
    Reenter,
    Refuse,
    Abandon,
    Replay
}
