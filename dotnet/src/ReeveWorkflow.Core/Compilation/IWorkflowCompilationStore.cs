namespace ReeveWorkflow.Core.Compilation;

/// <summary>One workflow version's compiled verdict against one catalogue and one resolved port environment.
/// <see cref="BlockSources"/> is every non-null <see cref="BlockShape.Source"/> among the shapes the compile
/// reached, deduped and sorted — the provenance that turns "seven workflows are broken" into "seven
/// workflows are broken by this plugin update".</summary>
/// <remarks>Identified by <see cref="WorkflowVersionId"/>, <see cref="DefinitionDigest"/> and
/// <see cref="DependencyHash"/> together, never a pair: a draft is edited in place through
/// <c>DraftVersion.UpdateDefinition</c>, so its version id alone does not identify what was compiled, and a
/// dependency hash is shared by every version whose catalogue-reaching shapes render alike. Two versions can
/// share a dependency hash; a version's own history moves its definition digest each time it is saved.</remarks>
public sealed record CompilationVerdict(
    Guid WorkflowVersionId,
    string DefinitionDigest,
    string DependencyHash,
    bool IsValid,
    IReadOnlyList<CompilationDiagnostic> Diagnostics,
    IReadOnlyList<string> BlockSources,
    string CompilerVersion,
    DateTimeOffset CompiledAtUtc);

/// <summary>What identifies one stored verdict: the version, the digest of the documents compiled, and the
/// dependency hash, as <see cref="CompilationVerdict"/> carries them.</summary>
public sealed record CompilationKey(Guid WorkflowVersionId, string DefinitionDigest, string DependencyHash)
{
    /// <summary>The key <paramref name="verdict"/> is stored under.</summary>
    public static CompilationKey Of(CompilationVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return new CompilationKey(verdict.WorkflowVersionId, verdict.DefinitionDigest, verdict.DependencyHash);
    }
}

/// <summary>Where a <see cref="CompilationVerdict"/> is persisted. Optional at the point that uses it —
/// <c>WorkflowCompilationService</c> — without one, compilation is memoized in-process, correctness is
/// unchanged, and what is lost is the report and the memory between restarts: the same shape as
/// <c>IWorkflowSuspensionStore</c>.</summary>
public interface IWorkflowCompilationStore
{
    /// <summary>The verdict stored under this exact triple, or null on a miss — a miss the caller then
    /// compiles for and saves, never a reason to fall back to a different key.</summary>
    Task<CompilationVerdict?> FindAsync(
        Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken);

    /// <summary>Upserts by the verdict's own <see cref="CompilationVerdict.WorkflowVersionId"/>,
    /// <see cref="CompilationVerdict.DefinitionDigest"/> and <see cref="CompilationVerdict.DependencyHash"/>.
    /// Two replicas racing the same triple need not write byte-identical rows: <see cref="BlockShape.Source"/>
    /// is deliberately excluded from the hash, so one replica's <see cref="CompilationVerdict.BlockSources"/>
    /// can name an older plugin version than another's for the very same triple — either write may still
    /// win, but a reader of the row that lost the race may see a source string one recompile out of date.</summary>
    Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken);

    /// <summary>Deletes two kinds of row older than <paramref name="olderThan"/>: a row of a version
    /// <paramref name="current"/> names that is under another digest or hash than that version's key, and every
    /// row of a version that no longer exists. A version <paramref name="current"/> does not name keeps its rows
    /// while it exists, so a partial or empty collection deletes less, never a row a version needs. Retention, not
    /// correctness: a deleted row costs a recompile.</summary>
    /// <remarks>An implementation must check the age when it deletes, on its own clock: a row goes only if its
    /// <see cref="CompilationVerdict.CompiledAtUtc"/> is older than <paramref name="olderThan"/> at the moment the
    /// statement that deletes it runs, so a row saved after the caller read its versions stays. That clock and the
    /// clock of the replica that compiled the row are assumed to differ by less than
    /// <see cref="WorkflowCompilationSweeper.MinimumRetention"/>, the smallest <paramref name="olderThan"/> accepted.</remarks>
    /// <param name="current">One key per version: the verdict a sweep answered for it.</param>
    /// <param name="olderThan">How old a row must be, by <see cref="CompilationVerdict.CompiledAtUtc"/>, to be deleted.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many rows were deleted.</returns>
    /// <exception cref="ArgumentException">A key is null, or its digest or hash is null, blank, or holds a NUL or
    /// an unpaired surrogate; or a version is named twice. Thrown before any command.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is shorter than
    /// <see cref="WorkflowCompilationSweeper.MinimumRetention"/>.</exception>
    Task<int> DeleteStaleAsync(
        IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken);
}

/// <summary>A verdict store declining a call because of how it is configured rather than because something
/// failed, so the next call gets the same answer. <c>EfWorkflowCompilationStore</c> throws it before any command.</summary>
/// <remarks><see cref="WorkflowCompilationService"/> treats a find declined this way as a miss and a save as not
/// saved, and logs the first one it sees once, without a stack trace. Any other exception keeps its logging.</remarks>
/// <param name="message">What the store saw and what it did not do.</param>
public sealed class CompilationStoreUnavailableException(string message) : InvalidOperationException(message);
