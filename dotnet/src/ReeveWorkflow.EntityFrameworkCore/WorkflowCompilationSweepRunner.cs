using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>What one compilation sweep did: its report, and how many verdict rows its retention deleted.</summary>
/// <param name="Report">What <see cref="WorkflowCompilationSweeper.SweepAsync"/> answered.</param>
/// <param name="Deleted">What <see cref="WorkflowCompilationSweeper.DeleteStaleAsync"/> answered.</param>
public sealed record WorkflowCompilationSweepRun(CompilationSweepReport Report, int Deleted);

/// <summary>One compilation sweep and its retention, run the way the timer runs them: one at a time in this process,
/// under <see cref="IWorkflowLeaderGate"/> with <see cref="LeaderLockKey"/>, and only once the database is ready.
/// <c>AddWorkflows</c> registers it whether or not <see cref="WorkflowCompilationSweepOptions.Enabled"/> is set, so a
/// host can call <see cref="RunOnceAsync"/> from its own endpoint or job. It hands the runner the registered gate, from
/// either side of that call, or, when none is registered, the one <c>WorkflowLeaderGates.DerivedFrom</c> gives; it
/// registers no gate itself.</summary>
/// <remarks>The whole run goes through the gate, because the retention deletes by the report its own replica built
/// and two retentions at once over one PostgreSQL database have deadlocked (<c>40P01</c>). A gate that grants every
/// call, such as <see cref="SingleReplicaLeaderGate"/>, cannot keep two runs in one process apart, so a run first
/// waits for the one before it here.
/// <para>Ready means what <c>RefuseUnlessReadyToSweepAsync</c> in <see cref="WorkflowDatabaseExtensions"/> accepts:
/// when the context's migrations assembly has migrations, the model has no change they miss and none is unapplied.</para>
/// <para>A run logs what it found: the counts at <see cref="LogLevel.Information"/>, then each version that does not
/// compile, could not be answered or was not saved, at <see cref="LogLevel.Warning"/>. A database that is not ready is
/// a <see cref="LogLevel.Warning"/> with the reason. Any other fault is an <see cref="LogLevel.Error"/>, with the
/// report's counts when the sweep got that far, and is rethrown; a cancellation the caller asked for is neither.</para>
/// <para>The listing and the retention read this package's tables, whatever fills <c>Seams.Store</c>:
/// <see cref="EfCurrentWorkflowVersions"/> reads <c>workflows</c>, and the retention deletes the old rows of every
/// version <c>workflow_versions</c> does not hold. A host whose store keeps its versions elsewhere lists none of
/// them, and each retention deletes every verdict row older than the retention. That costs recompiles, never a
/// wrong answer.</para></remarks>
public sealed class WorkflowCompilationSweepRunner
{
    /// <summary>The leader key the run is gated on, beside the retention sweeps' own.</summary>
    public const string LeaderLockKey = "WorkflowCompilationSweep_Leader";

    private readonly IServiceScopeFactory scopes;
    private readonly WorkflowCompilationSweepOptions options;
    private readonly IWorkflowLeaderGate leader;
    private readonly ILogger logger;
    private readonly SemaphoreSlim oneRunAtATime = new(1, 1);

    /// <param name="scopes">Where each run's scope, and each page of the listing, come from.</param>
    /// <param name="options">The listing's <see cref="WorkflowCompilationSweepOptions.PageSize"/>.</param>
    /// <param name="leader">Which replica runs.</param>
    /// <param name="logger">Where the run says what it found.</param>
    public WorkflowCompilationSweepRunner(
        IServiceScopeFactory scopes, WorkflowCompilationSweepOptions options, IWorkflowLeaderGate leader,
        ILogger<WorkflowCompilationSweepRunner>? logger = null)
    {
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.leader = leader ?? throw new ArgumentNullException(nameof(leader));
        this.logger = logger ?? NullLogger<WorkflowCompilationSweepRunner>.Instance;
    }

    /// <summary>Sweeps every current version, then deletes the stale verdict rows, as the remarks describe.</summary>
    /// <returns>The run, or null when this replica did not lead or the database was not ready.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<WorkflowCompilationSweepRun?> RunOnceAsync(CancellationToken cancellationToken) =>
        (await RunAsync(cancellationToken).ConfigureAwait(false)).Run;

    internal async Task<(WorkflowCompilationSweepOutcome Outcome, WorkflowCompilationSweepRun? Run)> RunAsync(
        CancellationToken cancellationToken)
    {
        await oneRunAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        CompilationSweepReport? reached = null;
        try
        {
            var outcome = WorkflowCompilationSweepOutcome.NotLeading;
            WorkflowCompilationSweepRun? run = null;
            await leader.RunUnderLeaderAsync(LeaderLockKey, async token =>
            {
                (outcome, run) = await LeadAsync(report => reached = report, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return (outcome, run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (reached is { } report)
            {
                logger.LogError(ex, "The compilation sweep failed after it had answered: {Counts}.", Counts(report, deleted: null));
                LogVersions(report);
            }
            else
            {
                logger.LogError(ex, "The compilation sweep failed before it had answered any version.");
            }

            throw;
        }
        finally
        {
            oneRunAtATime.Release();
        }
    }

    private async Task<(WorkflowCompilationSweepOutcome, WorkflowCompilationSweepRun?)> LeadAsync(
        Action<CompilationSweepReport> reached, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                await WorkflowDatabaseExtensions.RefuseUnlessReadyToSweepAsync(
                    scope.ServiceProvider.GetRequiredService<WorkflowDbContext>(), cancellationToken).ConfigureAwait(false);
            }
            catch (WorkflowDatabaseNotReadyException notReady)
            {
                logger.LogWarning("The compilation sweep did not run: {Reason}", notReady.Message);
                return (WorkflowCompilationSweepOutcome.NotReady, null);
            }

            var sweeper = scope.ServiceProvider.GetRequiredService<WorkflowCompilationSweeper>();
            var listing = new EfCurrentWorkflowVersions(scopes, options.PageSize);
            var report = await sweeper.SweepAsync(listing.ReadAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            reached(report);
            var deleted = await sweeper.DeleteStaleAsync(report, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("The compilation sweep ran: {Counts}.", Counts(report, deleted));
            LogVersions(report);
            return (WorkflowCompilationSweepOutcome.Swept, new WorkflowCompilationSweepRun(report, deleted));
        }
    }

    private static string Counts(CompilationSweepReport report, int? deleted) =>
        $"{report.Current.Count} current, {report.Invalid.Count} invalid, {report.Failed.Count} failed, "
        + $"{report.Unsaved.Count} unsaved, {report.Shared.Count} shared, "
        + $"{report.MalformedPortTypeNamesInParameters} malformed port type names in parameters and "
        + $"{report.MalformedPortTypeNamesOnNodes} on nodes, {report.MalformedPortTypeNamesInTypeArguments} malformed type arguments, "
        + (deleted is { } rows ? $"{rows} rows deleted" : "the retention did not finish");

    private void LogVersions(CompilationSweepReport report)
    {
        foreach (var verdict in report.Invalid)
        {
            logger.LogWarning("Workflow version {WorkflowVersionId} does not compile: {Rules}.",
                verdict.WorkflowVersionId, string.Join(", ", verdict.Diagnostics.Select(d => d.RuleId).Distinct(StringComparer.Ordinal)));
        }

        foreach (var failure in report.Failed)
            logger.LogWarning(failure.Error, "The compilation sweep could not answer workflow version {WorkflowVersionId}.", failure.WorkflowVersionId);

        foreach (var versionId in report.Unsaved)
            logger.LogWarning("The verdict of workflow version {WorkflowVersionId} was not saved.", versionId);
    }
}

internal enum WorkflowCompilationSweepOutcome
{
    Swept,
    NotLeading,
    NotReady,
}
