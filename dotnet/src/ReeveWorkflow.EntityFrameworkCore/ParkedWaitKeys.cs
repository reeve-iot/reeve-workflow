using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

internal sealed class ParkedWaitKeys(IReadOnlyList<WorkflowExpressionNamespace> registrations)
{
    private readonly ExpressionDependencyWalker walker =
        ExpressionDependencyWalker.For(registrations ?? throw new ArgumentNullException(nameof(registrations)));

    public IReadOnlySet<string> Of(WorkflowSuspensionKind kind, string configJson) =>
        WorkflowSuspensionWaker.DependencyKeysOf(kind, configJson, walker, WorkflowGraphHelpers.JsonOptions);
}
