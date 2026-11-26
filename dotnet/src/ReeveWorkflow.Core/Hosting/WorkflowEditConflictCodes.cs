namespace ReeveWorkflow.Core.Hosting;

/// <summary>The codes of the editor's 409 answers that tell a caller which kind of overtaken write it made; an answer
/// without one is a write another write beat to the draft or to the version history.</summary>
public static class WorkflowEditConflictCodes
{
    /// <summary>The update named the revision its caller read, and another write had moved the workflow since; read it
    /// again and apply the change to what it holds now.</summary>
    public const string RevisionMismatch = "revision-mismatch";
}
