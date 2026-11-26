namespace ReeveWorkflow.Core.Execution;

public sealed class WorkflowNodeExecutionException : Exception
{
    public string NodeId { get; }
    public string BlockTypeId { get; }

    public WorkflowNodeExecutionException(string message, string nodeId, string blockTypeId, Exception? innerException = null)
        : base(message, innerException)
    {
        NodeId = nodeId;
        BlockTypeId = blockTypeId;
    }
}
