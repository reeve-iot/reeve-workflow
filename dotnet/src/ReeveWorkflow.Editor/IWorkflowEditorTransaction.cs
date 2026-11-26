using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor;

/// <summary>In whose transaction a write is made. The library's default opens one on its own context; a
/// host that writes its own rows in the same unit of work fills this seam and hands back its own.</summary>
/// <remarks>The seam exists because an import is two writes in one intent — the host's own rows and the library's
/// workflow — and a library that always opened its own would make them two. Editor set-based writes to Workflow and
/// WorkflowVersion carry the cluster's appended-to-change-log tag. A host whose capture maps Workflow must append
/// its change in the scope's <see cref="IWorkflowEditorTransactionScope.CommitAsync"/>, possibly without a key, in
/// the same transaction. If an editor call throws in a joined scope, its owner must not commit: tagged writes may
/// have run without the append.</remarks>
public interface IWorkflowEditorTransaction
{
    /// <summary>Begins, or joins, the transaction this write belongs in.</summary>
    Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins the transaction for a create or update, before the workflow row is read, knowing what it will
    /// store. Other writes call the overload above.</summary>
    Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="BeginAsync(CancellationToken)"/>, called now, would join a transaction someone else
    /// commits. Both overloads must decide alike, as the editor asks this before calling either.</summary>
    /// <remarks>Then nothing outside that transaction can read what the write stores, the compiler included, so the
    /// editor saves without compiling or promoting (<see cref="WorkflowEditResult.PublishDeferred"/>), and refuses a
    /// publish or a rollback (<see cref="WorkflowPublishInHostTransactionException"/>). Publish after the commit.</remarks>
    bool JoinsAnotherTransaction { get; }
}

/// <summary>What a create or an update is about to store, handed to <see cref="IWorkflowEditorTransaction.BeginAsync(WorkflowWriteIntent, CancellationToken)"/>
/// before the workflow row is read.</summary>
/// <param name="WorkflowId">The workflow being updated; null on a create.</param>
/// <param name="HostMetadata">The metadata the row will hold after the write, as stored: null for none, and for an empty map.</param>
public sealed record WorkflowWriteIntent(Guid? WorkflowId, IReadOnlyDictionary<string, string>? HostMetadata);

/// <summary>One transaction, from the writer's side. Disposing without committing rolls back, so a write
/// that throws leaves nothing behind whether or not its caller remembered to say so.</summary>
/// <remarks>An implementation that rolls back MUST clear the context's change tracker. Entity Framework
/// accepts its changes when <c>SaveChanges</c> returns, before the transaction commits, so a scope that
/// rolls back without clearing leaves the context describing rows that no longer exist — a later read in
/// the same scope answers from that, and a later save writes nothing and says nothing. For a host whose capture maps
/// Workflow, append its change here in the same transaction, possibly without a key. A joined scope does not commit,
/// but the host still appends the change here; if an editor call throws, its owner must not commit the outer
/// transaction because tagged writes may have run without the append.</remarks>
public interface IWorkflowEditorTransactionScope : IAsyncDisposable
{
    /// <summary>Commits an owned transaction. A joined scope does not commit, but the host still appends the workflow
    /// change here before the owner commits, when its capture maps Workflow.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The library's own answer: a transaction on the workflow context. A host that has already begun
/// one on that context gets a scope that joins it rather than a provider error.</summary>
public sealed class WorkflowContextEditorTransaction(WorkflowDbContext context) : IWorkflowEditorTransaction
{
    /// <inheritdoc />
    public bool JoinsAnotherTransaction => context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
        BeginAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is not null)
            return BorrowedScope.Instance;

        try
        {
            return new OwnedScope(
                context, await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "The workflow editor could not open a transaction. A host that begins one on the database "
                + "connection or in an ambient TransactionScope, rather than on the workflow context, must fill "
                + "the Editor.Transaction seam so the library joins it instead of opening its own.", exception);
        }
    }

    private sealed class OwnedScope(WorkflowDbContext context, IDbContextTransaction transaction)
        : IWorkflowEditorTransactionScope
    {
        private bool committed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync().ConfigureAwait(false);

            if (!committed)
                context.ChangeTracker.Clear();
        }
    }

    private sealed class BorrowedScope : IWorkflowEditorTransactionScope
    {
        public static readonly BorrowedScope Instance = new();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
