using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>One dependency-injection scope per run, resolved once and handed over as a group. The engine never
/// learns what a service provider is; it gets the four things whose lifetime is the run's.</summary>
/// <remarks>The store and the journal are the scope's own <c>IWorkflowStore</c> and
/// <c>IWorkflowRunJournal</c>: what <c>Seams.Store</c> and <c>Seams.RunJournal</c> hold, or a host's own
/// registration of either type, which the last registration decides.</remarks>
public sealed class EfWorkflowRunScopeFactory(IServiceScopeFactory scopeFactory) : IWorkflowRunScopeFactory
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public IWorkflowRunScope CreateScope() => new EfWorkflowRunScope(scopeFactory.CreateScope());
}

/// <summary>The scope itself, over one <see cref="WorkflowDbContext"/>.</summary>
/// <remarks>Everything inside is resolved eagerly, because the engine holds this for the whole run: a lazy
/// resolve would move a container failure from startup of the run to an arbitrary point inside one.</remarks>
public sealed class EfWorkflowRunScope : IWorkflowRunScope
{
    private readonly IServiceScope scope;

    /// <param name="scope">The scope to own. Disposed by this object, including when the constructor throws.</param>
    public EfWorkflowRunScope(IServiceScope scope)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        try
        {
            scope.ServiceProvider.GetService<SingletonJournalRefusal>()?.ThrowIfASingletonAnswers();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            Store = scope.ServiceProvider.GetService<IWorkflowStore>() ?? new EfWorkflowStore(db);
            Journal = scope.ServiceProvider.GetService<IWorkflowRunJournal>() ?? new EfWorkflowRunJournal(
                db, scope.ServiceProvider.GetService<IEnumerable<IWorkflowRunLogContributor>>());
            Audit = scope.ServiceProvider.GetRequiredService<IWorkflowAuditSink>();
            Authorization = scope.ServiceProvider.GetRequiredService<IWorkflowAuthorization>();
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public IServiceProvider Services => scope.ServiceProvider;

    /// <inheritdoc />
    public IWorkflowStore Store { get; }

    /// <inheritdoc />
    public IWorkflowRunJournal Journal { get; }

    /// <inheritdoc />
    public IWorkflowAuditSink Audit { get; }

    /// <inheritdoc />
    public IWorkflowAuthorization Authorization { get; }

    /// <inheritdoc />
    public void Dispose() => scope.Dispose();
}
