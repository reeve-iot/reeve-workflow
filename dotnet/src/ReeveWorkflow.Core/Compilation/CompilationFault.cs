namespace ReeveWorkflow.Core.Compilation;

internal static class CompilationFault
{
    public static bool Catches(Exception exception) => exception is not (OperationCanceledException or OutOfMemoryException);

    public static T AtNode<T>(IReadOnlyList<CanvasPathSegment> canvasPath, string nodeId, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception exception) when (Catches(exception))
        {
            throw new NodeFaultException(canvasPath, nodeId, exception);
        }
    }

    public static CompilationDiagnostic Diagnostic(string phase, CanvasPath path, Exception exception, out Exception cause)
    {
        var node = exception as NodeFaultException;
        cause = node?.InnerException ?? exception;
        var at = node is null ? "" : $" at node '{node.NodeId}'";
        return new CompilationDiagnostic(
            CompilationRules.CompilationFault,
            $"The compiler could not finish its '{phase}' phase{at}: {cause.GetType().Name}: {cause.Message}",
            node?.NodeId, CanvasPath: node?.CanvasPath ?? path.Segments);
    }

    private sealed class NodeFaultException(IReadOnlyList<CanvasPathSegment> canvasPath, string nodeId, Exception inner)
        : Exception(inner.Message, inner)
    {
        public IReadOnlyList<CanvasPathSegment> CanvasPath { get; } = canvasPath;
        public string NodeId { get; } = nodeId;
    }
}
