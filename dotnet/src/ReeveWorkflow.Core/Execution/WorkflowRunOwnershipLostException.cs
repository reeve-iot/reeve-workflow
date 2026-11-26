namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown by the graph walk when the run's state row is no longer this replica's to write. The walk
/// stops before the next block so the run is not executed twice.</summary>
/// <remarks>Escapes every error policy and every try/catch container on purpose — it is not a block's failure —
/// and the runner that catches it must not end the row, which is no longer this replica's to end.</remarks>
public sealed class WorkflowRunOwnershipLostException : Exception
{
    /// <summary>A loss whose reason was not established. Kept for callers compiled against the earlier signature.</summary>
    public WorkflowRunOwnershipLostException(Guid stateExecutionId, string nodeId)
        : this(stateExecutionId, nodeId, WorkflowRunOwnershipLoss.Unknown)
    {
    }

    /// <summary>A loss whose reason was read back from the row, which is what the message then says.</summary>
    public WorkflowRunOwnershipLostException(Guid stateExecutionId, string nodeId, WorkflowRunOwnershipLoss loss)
        : base(MessageFor(stateExecutionId, nodeId, loss))
    {
        StateExecutionId = stateExecutionId;
        NodeId = nodeId;
        Loss = loss;
    }

    public Guid StateExecutionId { get; }

    public string NodeId { get; }

    /// <summary>Why the row stopped being this replica's, as far as the row itself could say.</summary>
    public WorkflowRunOwnershipLoss Loss { get; }

    private static string MessageFor(Guid stateExecutionId, string nodeId, WorkflowRunOwnershipLoss loss)
    {
        var why = loss switch
        {
            WorkflowRunOwnershipLoss.ReclaimedByPeer => "was reclaimed by another replica",
            WorkflowRunOwnershipLoss.Released => "was released and names no owner",
            WorkflowRunOwnershipLoss.StatusMoved => "was moved to a status this run did not write",
            WorkflowRunOwnershipLoss.RowMissing => "no longer exists",
            _ => "refused this replica's write and could not be read back to say why"
        };

        var where = string.IsNullOrEmpty(nodeId) ? "while a block was running" : $"before node '{nodeId}'";
        return $"run_ownership_lost: state row {stateExecutionId} {why} {where}; this replica stops so the run is "
            + "not executed twice.";
    }
}

/// <summary>What a run's state row said when this replica's fenced write to it was refused. A store answers
/// every one of these with the same <c>false</c>; the row read back afterwards is what tells them apart.</summary>
public enum WorkflowRunOwnershipLoss
{
    /// <summary>The row could not be read back, or read back exactly as the write required — something moved
    /// it and moved it back. Nothing is claimed about who has it.</summary>
    Unknown,

    /// <summary>The row names a different replica: a peer presumed this one dead and took it over.</summary>
    ReclaimedByPeer,

    /// <summary>The row names no replica — handed over on a stop, or written by a host that names none.</summary>
    Released,

    /// <summary>The row is in a status the run did not put it in: cancelled or ended by something else.</summary>
    StatusMoved,

    /// <summary>There is no row under that id any more.</summary>
    RowMissing
}
