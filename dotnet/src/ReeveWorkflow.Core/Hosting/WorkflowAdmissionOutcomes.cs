namespace ReeveWorkflow.Core.Hosting;

/// <summary>What arbitration decided about one attempt to start a run. Closed set: the host records these as a
/// metric label and picks an audit event by them, so a value outside it is a bug on both sides.</summary>
public static class WorkflowAdmissionOutcomes
{
    /// <summary>A slot was taken and the run may execute.</summary>
    public const string Started = "started";

    /// <summary>A run of this workflow was already in flight and this attempt yields to it.</summary>
    public const string Skipped = "skipped";

    /// <summary>A slot was taken by cancelling the run this attempt supersedes.</summary>
    public const string Restarted = "restarted";

    /// <summary>The attempt waited for a slot rather than being refused, and then got one.</summary>
    public const string Queued = "queued";

    /// <summary>No slot and no room to wait, so the attempt was discarded.</summary>
    public const string Dropped = "dropped";

    /// <summary>Refused loudly rather than discarded, because the workflow asked to be told.</summary>
    public const string Errored = "errored";
}
