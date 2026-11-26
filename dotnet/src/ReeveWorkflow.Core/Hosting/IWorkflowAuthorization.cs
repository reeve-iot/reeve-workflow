namespace ReeveWorkflow.Core.Hosting;

/// <summary>The one question the engine asks about a principal: may it do this. Roles, grants, inheritance
/// and their storage stay the host's; the principal is an opaque id the library never interprets.</summary>
public interface IWorkflowAuthorization
{
    /// <summary>False denies. A host that cannot answer must throw rather than return false, so a broken
    /// lookup is never mistaken for a decision to deny.</summary>
    Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken = default);
}

/// <summary>Capabilities the engine checks. The names are the library's; a host maps them onto whatever its
/// own permission vocabulary calls the same thing.</summary>
public static class WorkflowCapabilities
{
    /// <summary>Demanded of a workflow's owner before a version containing a scripting block may run.</summary>
    public const string Scripting = "workflow.scripting";
}
