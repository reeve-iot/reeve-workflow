using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Execution;

internal readonly record struct WorkflowFailureCode(string Code, string? Category);

internal static class WorkflowFailureCoding
{
    public static WorkflowFailureCode Of(Exception exception)
    {
        var code = WorkflowErrorCodes.BlockFailed;
        if (exception is IWorkflowCodedException coded)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(coded.Code))
                    code = coded.Code;
            }
            catch (Exception)
            {
            }
        }

        return new WorkflowFailureCode(code, exception switch
        {
            ExpressionEvaluationException evaluation => evaluation.Category,
            JsonProjectionException projection => projection.Category,
            WorkflowResumedFaultException fault => fault.Category,
            _ => null,
        });
    }

    public static WorkflowFailureCode OfNode(WorkflowNodeExecutionException failure) => Of(failure.InnerException ?? failure);
}
