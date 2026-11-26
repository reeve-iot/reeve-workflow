using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks;

public sealed class TryCatchBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("TryCatch block requires a SubCanvasExecutor.");

        var merged = new Dictionary<string, object?>();
        var caughtError = false;
        string? errorMessage = null;

        var tryResult = await context.SubCanvasExecutor.TryExecuteAsync("Try", inputs, cancellationToken).ConfigureAwait(false);
        foreach (var kvp in tryResult.Outputs) merged[kvp.Key] = kvp.Value;

        SubCanvasError? catchError = null;
        SubCanvasError? finallyError = null;
        SubCanvasRethrowException? catchRethrow = null;
        SubCanvasRethrowException? finallyRethrow = null;

        try
        {
            if (tryResult.Error != null)
            {
                caughtError = true;
                errorMessage = tryResult.Error.Message;

                var catchInputs = new Dictionary<string, object?>(inputs)
                {
                    ["Code"] = tryResult.Error.Code,
                    ["Category"] = tryResult.Error.Category,
                    ["Message"] = tryResult.Error.Message,
                    ["ExceptionType"] = tryResult.Error.ExceptionType,
                    ["FailedNodeId"] = tryResult.Error.FailedNodeId,
                    ["FailedBlockTypeId"] = tryResult.Error.FailedBlockTypeId
                };

                try
                {
                    var catchResult = await context.SubCanvasExecutor.TryExecuteAsync("Catch", catchInputs, cancellationToken).ConfigureAwait(false);
                    foreach (var kvp in catchResult.Outputs) merged[kvp.Key] = kvp.Value;
                    catchError = catchResult.Error;
                }
                catch (SubCanvasRethrowException rethrow)
                {
                    catchRethrow = rethrow;
                }
            }
        }
        finally
        {
            try
            {
                var finallyResult = await context.SubCanvasExecutor.TryExecuteAsync("Finally", inputs, cancellationToken).ConfigureAwait(false);
                foreach (var kvp in finallyResult.Outputs) merged[kvp.Key] = kvp.Value;
                finallyError = finallyResult.Error;
            }
            catch (SubCanvasRethrowException rethrow)
            {
                finallyRethrow = rethrow;
            }
        }

        var finallyFailed = finallyError != null || finallyRethrow != null;
        if (catchRethrow != null && !finallyFailed)
            throw catchRethrow;
        if (finallyRethrow != null && catchRethrow == null && catchError == null && tryResult.Error == null)
            throw finallyRethrow;

        var unhandled = new List<Exception>();

        if (tryResult.Error != null && (catchError != null || catchRethrow != null || finallyFailed))
            unhandled.Add(ToException(tryResult.Error));

        if (catchError != null)
            unhandled.Add(ToException(catchError));

        if (finallyError != null)
            unhandled.Add(ToException(finallyError));
        else if (finallyRethrow != null)
            unhandled.Add(finallyRethrow);

        if (unhandled.Count > 0)
            throw new AggregateException("TryCatch block completed with unhandled sub-canvas errors.", unhandled);

        merged["CaughtError"] = caughtError;
        merged["ErrorMessage"] = errorMessage ?? "";
        return merged;
    }

    private static Exception ToException(SubCanvasError error) =>
        new WorkflowNodeExecutionException(error.Message, error.FailedNodeId ?? "", error.FailedBlockTypeId ?? "");
}

public sealed class TryCatchBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "TryCatch";
    public string DisplayName => "Try / Catch";
    public string Description => "Handles errors in the 'Try' sub-canvas using the 'Catch' sub-canvas. Always executes 'Finally'.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs Try, Catch and Finally whole, so any of the three runs a second time.</summary>
    public bool IsIdempotent => false;
    /// <summary>Whether Catch runs is whether Try threw, and a replayed invocation carries the error it ended with,
    /// so the same three calls are made again.</summary>
    public bool OnlyDrivesItsSubCanvases => true;
    public bool SupportsDeclaredOutputs => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("CaughtError", "Caught Error", PortValueType.Boolean, false),
        new BlockPortDefinition("ErrorMessage", "Error Message", PortValueType.String, false)
    ];

    public IReadOnlyCollection<string> SubCanvasNames => ["Try", "Catch", "Finally"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName)
    {
        if (subCanvasName == "Catch")
        {
            return
            [
                new BlockPortDefinition("Code", "Error Code", PortValueType.String, false),
                new BlockPortDefinition("Category", "Error Category", PortValueType.Of("String?"), false),
                new BlockPortDefinition("Message", "Error Message", PortValueType.String, false),
                new BlockPortDefinition("ExceptionType", "Exception Type", PortValueType.String, false),
                new BlockPortDefinition("FailedNodeId", "Failed Node ID", PortValueType.Of("String?"), false),
                new BlockPortDefinition("FailedBlockTypeId", "Failed Block Type ID", PortValueType.Of("String?"), false)
            ];
        }
        return [];
    }

    public IWorkflowBlockPlugin Create() => new TryCatchBlock();
}
