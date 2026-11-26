using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Tests.Compilation;

internal sealed class CompilationScopes(
    IWorkflowStore store, IWorkflowCompilationStore? verdicts = null, IEnumerable<INodePortResolver>? resolvers = null)
    : IWorkflowRunScopeFactory
{
    private readonly IWorkflowStore store = store;
    private readonly IWorkflowCompilationStore? verdicts = verdicts;
    private readonly INodePortResolver[]? resolvers = resolvers?.ToArray();
    private int opened;
    private int disposed;

    public int Opened => Volatile.Read(ref opened);

    public int Disposed => Volatile.Read(ref disposed);

    public static WorkflowCompilationService Service(
        IWorkflowBlocks blocks,
        IWorkflowStore workflowStore,
        IWorkflowCompilationStore? verdictStore = null,
        CompilationOptions? options = null,
        IEnumerable<INodePortResolver>? resolvers = null,
        ILogger<WorkflowCompilationService>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(blocks, new CompilationScopes(workflowStore, verdictStore, resolvers), options, logger, timeProvider);

    public IWorkflowRunScope CreateScope()
    {
        Interlocked.Increment(ref opened);
        return new Scope(this);
    }

    private sealed class Scope(CompilationScopes owner) : IWorkflowRunScope, IServiceProvider
    {
        public IServiceProvider Services => this;

        public IWorkflowStore Store => owner.store;

        public IWorkflowRunJournal Journal => throw new NotSupportedException("A compile writes no journal.");

        public IWorkflowAuditSink Audit => throw new NotSupportedException("A compile audits nothing.");

        public IWorkflowAuthorization Authorization => throw new NotSupportedException("A compile authorizes nothing.");

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWorkflowCompilationStore) ? owner.verdicts
            : serviceType == typeof(IEnumerable<INodePortResolver>) ? owner.resolvers
            : null;

        public void Dispose() => Interlocked.Increment(ref owner.disposed);
    }
}
