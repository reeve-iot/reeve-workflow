using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>A value a block wrote, or a container handed its sub-canvas, is off the term of its port. The node fails
/// under its error policy, and the failure's code is <see cref="WorkflowErrorCodes.BlockOutputTypeMismatch"/>.</summary>
public sealed class WorkflowOutputTypeMismatchException : Exception, IWorkflowCodedException
{
    /// <param name="nodeId">The node whose block wrote the output, or the container that handed the input.</param>
    /// <param name="subCanvas">The sub-canvas the input was handed to; null for an output of the node itself.</param>
    /// <param name="port">The output, or the sub-canvas input.</param>
    /// <param name="reason">What is wrong with the value.</param>
    public WorkflowOutputTypeMismatchException(string nodeId, string? subCanvas, string port, string reason)
        : base(subCanvas is null
            ? $"{WorkflowErrorCodes.BlockOutputTypeMismatch}: output '{port}' of node '{nodeId}': {reason}."
            : $"{WorkflowErrorCodes.BlockOutputTypeMismatch}: input '{port}' of sub-canvas '{subCanvas}' of node '{nodeId}': {reason}.")
    {
        NodeId = nodeId;
        SubCanvas = subCanvas;
        Port = port;
    }

    /// <summary>Always <see cref="WorkflowErrorCodes.BlockOutputTypeMismatch"/>.</summary>
    public string Code => WorkflowErrorCodes.BlockOutputTypeMismatch;

    /// <summary>The node whose output was off its term, or whose sub-canvas input was.</summary>
    public string NodeId { get; }

    /// <summary>The sub-canvas whose input was off its term; null when the value was a node's own output.</summary>
    public string? SubCanvas { get; }

    /// <summary>The output port, or the sub-canvas input, the value was written to.</summary>
    public string Port { get; }
}
