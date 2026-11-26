namespace ReeveWorkflow.Abstractions;

/// <summary>Everything a workflow block may reach outside its own inputs: the gated-capability lookup and the effective granted set. The host owns the grant logic; this only names the question a block may ask.</summary>
public interface IWorkflowHostServices
{
    /// <summary>Immutable copy of the effective (granted ∩ requested) permission codes.</summary>
    IReadOnlyCollection<string> GrantedPermissions { get; }

    /// <summary>The gated sub-facade T if its mapped permission is granted, otherwise null — never a working object for an ungranted capability, and never a throw.</summary>
    T? TryGet<T>() where T : class;

    /// <summary>Convenience: true iff <see cref="TryGet{T}"/> would return non-null.</summary>
    bool Has<T>() where T : class => TryGet<T>() is not null;
}
