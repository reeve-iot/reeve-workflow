namespace ReeveWorkflow.Abstractions;

public interface ISubCanvasExecutor
{
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken cancellationToken);

    Task<SubCanvasResult> TryExecuteAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken cancellationToken);
}
