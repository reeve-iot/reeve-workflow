using Microsoft.EntityFrameworkCore;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>What the adopter tells <c>AddWorkflows</c>: how to reach the database. The schema, its migrations and
/// the history table that records them belong to the provider package, as <c>AddPostgreSqlSchema</c>.</summary>
public sealed class WorkflowPersistenceOptions
{
    private readonly List<IWorkflowDbContextConfigurator> configurators = [];

    /// <summary>Constructed by <c>AddWorkflows</c>. The slots read <see cref="Defaults"/> when they build the
    /// library's own answers, so the two are wired together here rather than by the caller.</summary>
    public WorkflowPersistenceOptions() => Seams = new WorkflowHostSeams(Defaults);

    /// <summary>Settings for the host seams <c>AddWorkflows</c> supplies a default for. A host that fills the
    /// matching slot in <see cref="Seams"/> never touches this.</summary>
    public WorkflowHostDefaultsOptions Defaults { get; } = new();

    /// <summary>Where a host names its own implementation of each seam, beside the connection string it belongs
    /// with rather than somewhere else in the container. A slot left alone keeps the library's answer.</summary>
    public WorkflowHostSeams Seams { get; }

    /// <summary>The compilation sweep: off on a timer unless <see cref="WorkflowCompilationSweepOptions.Enabled"/> is
    /// set, and always there to call on demand through <see cref="WorkflowCompilationSweepRunner"/>.</summary>
    public WorkflowCompilationSweepOptions CompilationSweep { get; } = new();

    internal IReadOnlyList<IWorkflowDbContextConfigurator> Configurators => configurators;

    /// <summary>Adds one mutation of the context's options — the provider, the connection string, timeouts.
    /// Callable more than once; every registered mutation runs, in the order it was added.</summary>
    public WorkflowPersistenceOptions ConfigureDbContext(Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configurators.Add(new DelegateWorkflowDbContextConfigurator(configure));
        return this;
    }
}

/// <summary>One mutation of the workflow context's options, applied when the container builds the context rather
/// than when it was registered. That timing is the whole point: see <c>AddWorkflows</c>.</summary>
public interface IWorkflowDbContextConfigurator
{
    /// <summary>Applies this mutation.</summary>
    void Configure(DbContextOptionsBuilder optionsBuilder);
}

/// <summary>An <see cref="IWorkflowDbContextConfigurator"/> over a lambda, so an add-on package needs no type.</summary>
public sealed class DelegateWorkflowDbContextConfigurator(Action<DbContextOptionsBuilder> configure)
    : IWorkflowDbContextConfigurator
{
    private readonly Action<DbContextOptionsBuilder> configure =
        configure ?? throw new ArgumentNullException(nameof(configure));

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder) => this.configure(optionsBuilder);
}
