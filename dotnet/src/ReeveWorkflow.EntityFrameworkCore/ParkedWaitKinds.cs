using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

internal static class ParkedWaitKinds
{
    public static readonly IReadOnlyList<WorkflowSuspensionKind> Wakeable =
    [
        WorkflowSuspensionKind.WaitForTrigger,
        WorkflowSuspensionKind.WaitTemplate,
        WorkflowSuspensionKind.SubWorkflow,
        WorkflowSuspensionKind.Predicate
    ];

    public static readonly IReadOnlyList<WorkflowSuspensionKind> Keyed =
    [
        WorkflowSuspensionKind.WaitForTrigger,
        WorkflowSuspensionKind.WaitTemplate,
        WorkflowSuspensionKind.Predicate
    ];

    public static string SpeculativeIndexFilter =>
        $"\"status\" = '{nameof(WorkflowSuspensionStatus.Suspended)}' AND \"kind\" IN "
        + $"({string.Join(", ", Wakeable.Select(kind => (int)kind).Order())})";
}
