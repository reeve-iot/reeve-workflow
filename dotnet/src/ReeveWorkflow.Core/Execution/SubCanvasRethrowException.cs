namespace ReeveWorkflow.Core.Execution;

public sealed class SubCanvasRethrowException : Exception
{
    public SubCanvasRethrowException(string? message) : base(message)
    {
    }
}
