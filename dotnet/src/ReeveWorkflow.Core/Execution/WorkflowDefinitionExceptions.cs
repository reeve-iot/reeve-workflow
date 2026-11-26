namespace ReeveWorkflow.Core.Execution;

/// <summary>The graph named a block type nothing is registered under. A definition error, not an engine
/// fault, so a host maps it to 400 rather than 500.</summary>
public sealed class InvalidBlockTypeException(string blockTypeId) : Exception($"Block type '{blockTypeId}' is not available.")
{
    public string BlockTypeId { get; } = blockTypeId;
}

/// <summary>A workflow version does not read or does not hold together: its definition or a parameter list the
/// reader refuses, a duplicate node id, a setting the engine cannot apply. Same disposition as
/// <see cref="InvalidBlockTypeException"/>: the caller sent something invalid.</summary>
public sealed class InvalidWorkflowDefinitionException(string reason, Exception? innerException = null)
    : Exception($"The workflow is invalid: {reason}", innerException)
{
    public string Reason { get; } = reason;
}
