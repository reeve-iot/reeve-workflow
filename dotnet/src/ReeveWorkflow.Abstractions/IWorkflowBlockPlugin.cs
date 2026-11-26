namespace ReeveWorkflow.Abstractions;

public interface IWorkflowBlockPlugin
{
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken);
}
