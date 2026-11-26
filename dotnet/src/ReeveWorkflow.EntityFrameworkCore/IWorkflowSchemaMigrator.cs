namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Brings the workflow schema up to date. The migrations come from the provider package the host
/// composed (<c>AddPostgreSqlSchema</c>); this library takes no lock, so a host runs it under its own boot gate.</summary>
public interface IWorkflowSchemaMigrator
{
    /// <summary>Applies every outstanding migration, or throws <see cref="WorkflowDatabaseNotReadyException"/> when
    /// the model has a change no migration covers.</summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);
}
