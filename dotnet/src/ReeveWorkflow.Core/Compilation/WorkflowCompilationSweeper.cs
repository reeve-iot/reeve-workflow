using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>A version a sweep could not answer, because <see cref="WorkflowCompilationService.EnsureAsync"/> threw
/// for it, as it does for a fault from a resolver or from <c>IWorkflowStore</c>, and for a
/// <see cref="CompilationFaultException"/>.</summary>
/// <param name="WorkflowVersionId">The version.</param>
/// <param name="Error">What the service threw.</param>
public sealed record CompilationSweepFailure(Guid WorkflowVersionId, Exception Error);

/// <summary>A version that more than one workflow names as its current version. Nothing in the schema forbids it.</summary>
/// <param name="WorkflowVersionId">The version.</param>
/// <param name="WorkflowIds">Every workflow the listing gave it for, in the order listed.</param>
public sealed record CompilationSharedVersion(Guid WorkflowVersionId, IReadOnlyList<Guid> WorkflowIds);

/// <summary>A current version as a sweep is given it.</summary>
/// <param name="WorkflowId">The workflow whose current version this is, which need not be
/// <see cref="WorkflowVersion.WorkflowId"/>.</param>
/// <param name="Version">The version, with its documents as a run reads them.</param>
public sealed record CurrentWorkflowVersion(Guid WorkflowId, WorkflowVersion Version);

/// <summary>What one sweep found: the number a host logs after a deploy, and the list an operator reads. It covers
/// what the listing gave the sweep. A version the listing did not reach, such as one that became current behind a
/// paging cursor, is in no list; the next sweep's listing gives it.</summary>
/// <param name="Current">Every version the sweep answered, once each, in the order it was first given them, under
/// the key of the verdict it was answered with: its own documents' digest and its own dependency hash.</param>
/// <param name="Invalid">The verdicts among them that do not compile, with their diagnostics.</param>
/// <param name="Failed">Every version the sweep could not answer. None of them is in <paramref name="Current"/>.</param>
/// <param name="Unsaved">The versions in <paramref name="Current"/> whose verdict the store failed or declined to
/// save, as the service's memo holds it; the service logs each such fault. A row deleted after a save the store
/// accepted is not seen here.</param>
/// <param name="Shared">Every version given for more than one workflow. It is answered once: one id is one row.</param>
/// <param name="MalformedPortTypeNamesInParameters"><c>port-type-malformed</c> diagnostics in
/// <paramref name="Invalid"/> that name no node: a workflow's declared input or output parameter.</param>
/// <param name="MalformedPortTypeNamesOnNodes"><c>port-type-malformed</c> diagnostics in <paramref name="Invalid"/>
/// that name a node, excluding type arguments: a dynamic port, a declared output or a projection's type.</param>
/// <param name="MalformedPortTypeNamesInTypeArguments"><c>port-type-malformed</c> diagnostics in
/// <paramref name="Invalid"/> for type arguments.</param>
public sealed record CompilationSweepReport(
    IReadOnlyList<CompilationKey> Current,
    IReadOnlyList<CompilationVerdict> Invalid,
    IReadOnlyList<CompilationSweepFailure> Failed,
    IReadOnlyList<Guid> Unsaved,
    IReadOnlyList<CompilationSharedVersion> Shared,
    int MalformedPortTypeNamesInParameters,
    int MalformedPortTypeNamesOnNodes,
    int MalformedPortTypeNamesInTypeArguments);

/// <summary>Answers every current version it is given under the current catalogue, so the list of what a plugin
/// update broke exists before a trigger fires. A report, not a safety device: a run that meets a missing verdict
/// compiles it itself.</summary>
/// <remarks>Every answer comes from <see cref="WorkflowCompilationService.EnsureAsync"/> with no pin manifest, so a
/// sweep saves only what a miss saves. Run again once every verdict is stored, here or on another replica over the
/// same store and catalogue, it saves nothing, so <see cref="SweepAsync"/> needs no lock. <see cref="DeleteStaleAsync"/>
/// does: two at once over one PostgreSQL database have deadlocked (<c>40P01</c>), so run it on one replica at a time.
/// "Current" is per version, because a dependency hash is computed per workflow over the shapes its own definition
/// reaches: the report keeps each version's own key rather than asking the store for one hash's rows.</remarks>
/// <param name="service">What answers each version.</param>
/// <param name="store">What <see cref="DeleteStaleAsync"/> deletes from; null when verdicts are kept in-process only.</param>
public sealed class WorkflowCompilationSweeper(WorkflowCompilationService service, IWorkflowCompilationStore? store)
{
    private readonly WorkflowCompilationService service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>The shortest <see cref="Retention"/> accepted, and the shortest age at which a verdict store may
    /// delete a row. A row's age is its compiling replica's clock read against the deleting store's, so this is the
    /// clock skew between replicas that the retention assumes. It is an assumption, not a measurement.</summary>
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromHours(1);

    private readonly TimeSpan retention = TimeSpan.FromDays(30);

    /// <summary>How old a row must be before <see cref="DeleteStaleAsync"/> deletes it. Thirty days unless set.
    /// <see cref="TimeSpan.MaxValue"/> keeps every row.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is shorter than <see cref="MinimumRetention"/>.</exception>
    public TimeSpan Retention
    {
        get => retention;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, MinimumRetention);
            retention = value;
        }
    }

    /// <summary>Answers each version, one at a time. A version whose definition or parameters cannot be read is in
    /// <see cref="CompilationSweepReport.Invalid"/> with that rule; one the service throws on is in
    /// <see cref="CompilationSweepReport.Failed"/>, and the sweep goes on. A version given again, for another
    /// workflow or the same one, is not answered again: it is in <see cref="CompilationSweepReport.Shared"/>.</summary>
    /// <param name="currentVersions">Every current version. Its documents are the ones the version is answered for,
    /// so read them from where a run reads them.</param>
    /// <param name="cancellationToken">Stops the sweep, and is handed to the listing and to every answer; the sweep
    /// then throws rather than report what it reached.</param>
    /// <exception cref="ArgumentException">An entry or its version is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<CompilationSweepReport> SweepAsync(
        IAsyncEnumerable<CurrentWorkflowVersion> currentVersions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersions);

        var workflowsOf = new Dictionary<Guid, List<Guid>>();
        var current = new List<CompilationKey>();
        var invalid = new List<CompilationVerdict>();
        var failed = new List<CompilationSweepFailure>();
        var unsaved = new List<Guid>();

        await foreach (var entry in currentVersions.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry?.Version is not { } version)
                throw new ArgumentException("A current version is null.", nameof(currentVersions));

            if (workflowsOf.TryGetValue(version.Id, out var workflows))
            {
                workflows.Add(entry.WorkflowId);
                continue;
            }

            workflowsOf.Add(version.Id, [entry.WorkflowId]);
            CompilationVerdict verdict;
            bool saved;
            try
            {
                (verdict, saved) = await service.EnsureRecordedAsync(version, pinManifest: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(new CompilationSweepFailure(version.Id, ex));
                continue;
            }

            current.Add(CompilationKey.Of(verdict));
            if (!saved)
                unsaved.Add(version.Id);
            if (!verdict.IsValid)
                invalid.Add(verdict);
        }

        var shared = workflowsOf.Where(w => w.Value.Count > 1)
            .Select(w => new CompilationSharedVersion(w.Key, w.Value)).ToList();
        var malformed = invalid.SelectMany(v => v.Diagnostics).Where(d => d.RuleId == CompilationRules.PortTypeMalformed).ToList();
        return new CompilationSweepReport(
            current, invalid, failed, unsaved, shared,
            MalformedPortTypeNamesInParameters: malformed.Count(d => d.NodeId is null),
            MalformedPortTypeNamesOnNodes: malformed.Count(d => d.NodeId is not null && !IsTypeArgument(d)),
            MalformedPortTypeNamesInTypeArguments: malformed.Count(IsTypeArgument));
    }

    private static bool IsTypeArgument(CompilationDiagnostic d) =>
        d.NodeId is not null && d.Message.StartsWith(PortTypeNameSites.TypeArgumentLabel, StringComparison.Ordinal);

    /// <summary>Hands the store every key in <see cref="CompilationSweepReport.Current"/> with <see cref="Retention"/>:
    /// it deletes, older than that, each of those versions' rows under another key, and the rows of versions that no
    /// longer exist. A version the report does not name keeps its rows while it exists: one the sweep could not
    /// answer, a draft, an earlier version, and one the listing did not reach.</summary>
    /// <returns>How many rows the store deleted; 0 with no store, which holds no rows.</returns>
    /// <exception cref="CompilationStoreUnavailableException">The store declined, as it does on a connection
    /// that arrived open. Any other store fault reaches the caller too.</exception>
    public Task<int> DeleteStaleAsync(CompilationSweepReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (store is null)
            return Task.FromResult(0);

        return store.DeleteStaleAsync(report.Current, Retention, cancellationToken);
    }
}
