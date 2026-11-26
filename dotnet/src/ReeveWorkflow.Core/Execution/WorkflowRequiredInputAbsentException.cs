using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>A required input held no value when its node was about to run: no wire wrote one, the node's configuration has none, and the
/// expressions produced none. The node fails under its error policy and the block never runs.</summary>
public sealed class WorkflowRequiredInputAbsentException : Exception, IWorkflowCodedException
{
    public WorkflowRequiredInputAbsentException(string nodeId, string inputName)
        : base($"required-input-value-absent: input '{inputName}' of node '{nodeId}' is required and holds no value.")
    {
        NodeId = nodeId;
        InputName = inputName;
    }

    /// <summary>Always <see cref="WorkflowErrorCodes.RequiredInputValueAbsent"/>.</summary>
    public string Code => WorkflowErrorCodes.RequiredInputValueAbsent;

    public string NodeId { get; }

    public string InputName { get; }
}
