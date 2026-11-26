namespace ReeveWorkflow.Blocks;

/// <summary>Throw this from <see cref="HttpRequestBlock.ValidateUrl"/> to refuse a request: the block shapes it
/// into a failed response rather than an unexplained crash, the same way it shapes a timeout or a bad URL.</summary>
public sealed class WorkflowEgressBlockedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
