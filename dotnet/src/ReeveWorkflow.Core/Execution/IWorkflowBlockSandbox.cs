using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The confinement a block the host did not write is run in. No library default: a library that
/// answered this would be deciding what a plugin may reach, which is the one thing only the host knows.</summary>
/// <remarks>Unregistered means every block is the host's own and is handed the raw container — the right
/// answer for a host with no plugin model, and the only honest one for a library with no grant model. A sandboxed block is handed
/// <see cref="WorkflowBlockContext.Secrets"/> unwrapped: it reveals only the entries of its own node's stored literal, and the object
/// holds the host's codec, so a block that reflects into it is not confined by this record.</remarks>
public interface IWorkflowBlockSandbox
{
    /// <summary>The confinement for this factory, or null when it is the host's own block and needs none.</summary>
    Task<WorkflowBlockSandbox?> SandboxAsync(
        IWorkflowBlockFactory factory, IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>How a block that the host did not write is run: through a narrowed service provider, the host's own facade, and
/// the wraps' answers in place of the library's sub-canvas executor and expression evaluator, which the block never gets.</summary>
public sealed record WorkflowBlockSandbox(
    IWorkflowHostServices HostServices,
    IServiceProvider BlockServices,
    Func<ISubCanvasExecutor, ISubCanvasExecutor> WrapSubCanvasExecutor,
    Func<IWorkflowBlockExpressions, IWorkflowBlockExpressions> WrapExpressions);
