using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Resolves the host's audit sink inside a scope of its own, one per write, so a seam filled with a
/// scoped implementation can be read by the singletons that need it.</summary>
/// <remarks>MS.DI refuses to build a container in which a singleton captures a scoped service, and the engine,
/// the resumer and the debug session service are all singletons — a host whose sink writes through a unit of
/// work could not boot without a forwarder of its own. This is that forwarder, once, here.
///
/// A singleton sink resolved from a child scope is the same object, so this costs a scope and changes nothing
/// for a host whose sink was a singleton all along.</remarks>
public sealed class ScopeOpeningWorkflowAuditSink(IServiceScopeFactory scopeFactory) : IWorkflowAuditSink
{
    /// <inheritdoc />
    public async Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowAuditSink>()
            .WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
    }
}
