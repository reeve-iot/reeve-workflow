using System.Buffers;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The verdict store over the workflow context's own table. A singleton: every call opens a scope of
/// its own and works on that scope's context, never on a context its caller holds.</summary>
/// <remarks>A verdict is a cache entry of a deterministic function: a lost row costs one recompile and never
/// changes an answer. What the store promises its caller depends on the connection the scope's context
/// arrives with — closed, which the store's first command opens, or already open, which the store never
/// uses, because a transaction it cannot see may be on it:
/// <list type="table">
/// <listheader><term>Promise</term><description>Closed on arrival / open on arrival</description></listheader>
/// <item><term>1. Never commits or detaches the caller's pending changes.</term>
/// <description>Holds / holds: the caller's context is never touched, and on an open connection no command
/// runs.</description></item>
/// <item><term>2. Never dooms or aborts the caller's transaction.</term>
/// <description>Holds, because the ambient <see cref="TransactionScope"/> is suppressed and the caller's own
/// transaction is on another connection / holds, because no command runs.</description></item>
/// <item><term>3. Never blocks the caller on a resource the caller holds.</term>
/// <description>Not promised: a caller holding the pool's last connection or a SQLite write lock makes the
/// store wait out a timeout and fail, so call it outside your transaction / holds.</description></item>
/// <item><term>4. May lose a write, which costs a recompile.</term>
/// <description>A lost insert race finishes as an update; any other fault reaches the caller / every call is
/// skipped and the shape is logged once per instance: <see cref="FindAsync"/> answers null and the other two
/// throw <see cref="CompilationStoreUnavailableException"/>.</description></item>
/// </list>
/// <para><see cref="SaveAsync"/> upserts by <see cref="CompilationVerdict.WorkflowVersionId"/>,
/// <see cref="CompilationVerdict.DefinitionDigest"/> and <see cref="CompilationVerdict.DependencyHash"/>
/// together. Two writers racing the same triple both read no row and both try to insert; the loser's insert
/// hits the key the winner just claimed, which this store catches and finishes as an update of the winner's
/// row. <see cref="CompilationVerdict.CompiledAtUtc"/> is stored as the same instant in UTC, and read back with
/// offset zero.
/// It keeps every string of a verdict exactly as written or refuses the verdict: one holding a NUL or an
/// unpaired surrogate is refused with <see cref="ArgumentException"/> before any command runs.</para></remarks>
/// <param name="scopeFactory">Where each call's workflow context comes from.</param>
/// <param name="logger">Where a context whose connection arrived open is reported, once per instance.</param>
/// <param name="clock">What <see cref="DeleteStaleAsync"/> measures a row's age against; the system clock unless given.</param>
public sealed class EfWorkflowCompilationStore(
    IServiceScopeFactory scopeFactory, ILogger<EfWorkflowCompilationStore>? logger = null, TimeProvider? clock = null)
    : IWorkflowCompilationStore
{
    internal const int VersionsPerStatement = 1_000;

    private const string SharedConnection =
        "The workflow context this store resolved arrived with its connection already open, and a transaction "
        + "the store cannot see may be on it. The store runs no command on it";

    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly ILogger logger = logger ?? NullLogger<EfWorkflowCompilationStore>.Instance;

    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    private int sharedConnectionLogged;

    /// <inheritdoc />
    /// <remarks>A key carrying a NUL or an unpaired surrogate answers null without a query: no save accepts one.</remarks>
    public Task<CompilationVerdict?> FindAsync(
        Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
    {
        if (!KeptExactly(definitionDigest) || !KeptExactly(dependencyHash))
            return Task.FromResult<CompilationVerdict?>(null);

        return OnOwnContextAsync<CompilationVerdict?>(async db =>
        {
            var row = await db.WorkflowCompilations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowVersionId == workflowVersionId
                    && r.DefinitionDigest == definitionDigest && r.DependencyHash == dependencyHash, cancellationToken)
                .ConfigureAwait(false);

            return row is null ? null : ToVerdict(row);
        }, whenShared: () => null);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A string <paramref name="verdict"/> carries holds a NUL or an unpaired
    /// surrogate. Neither can be stored as written, and a rewritten node id would point at nothing.</exception>
    public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        foreach (var (site, text) in StringsOf(verdict))
        {
            if (!KeptExactly(text))
                throw new ArgumentException(
                    $"The verdict's {site} holds a NUL or an unpaired surrogate, which this store cannot keep as written.",
                    nameof(verdict));
        }

        return OnOwnContextAsync(async db =>
        {
            var existing = await FindRowAsync(db, verdict, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                Apply(existing, verdict);
                return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            db.WorkflowCompilations.Add(FromVerdict(verdict));
            try
            {
                return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var raced = await FindRowAsync(db, verdict, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                    throw;

                Apply(raced, verdict);
                return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }, whenShared: Refuse<int>(" and the verdict is not saved."));
    }

    /// <inheritdoc />
    /// <remarks>Set-based: one statement per <c>1 000</c> named versions deletes their old rows under another key,
    /// and one more deletes the old rows of every version <c>workflow_versions</c> no longer holds. Each statement
    /// carries the cutoff, read once from this store's clock, so a row saved meanwhile is younger than it and
    /// stays. A named version's row is kept when its digest and hash, joined as text with <c>|</c>, equal those of
    /// any key in its statement: its own key's row is always kept, and a row under another key is kept too when
    /// another named version in the statement has that digest and hash as its key. A retention reaching back past
    /// <see cref="DateTimeOffset.MinValue"/> deletes nothing and sends no command.</remarks>
    public Task<int> DeleteStaleAsync(
        IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken)
    {
        var keys = CurrentKeysOf(current);
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, WorkflowCompilationSweeper.MinimumRetention);

        var now = clock.GetUtcNow();
        if (olderThan >= now - DateTimeOffset.MinValue)
            return Task.FromResult(0);

        var cutoff = now - olderThan;
        return OnOwnContextAsync(async db =>
        {
            var deleted = 0;
            foreach (var statement in keys.Values.Chunk(VersionsPerStatement))
            {
                var versionIds = statement.Select(k => k.WorkflowVersionId).ToArray();
                var keptPairs = statement.Select(k => k.DefinitionDigest + "|" + k.DependencyHash).ToArray();
                deleted += await db.WorkflowCompilations
                    .Where(r => r.CompiledAtUtc < cutoff && versionIds.Contains(r.WorkflowVersionId)
                        && !keptPairs.Contains(r.DefinitionDigest + "|" + r.DependencyHash))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            deleted += await db.WorkflowCompilations
                .Where(r => r.CompiledAtUtc < cutoff && !db.WorkflowVersions.Any(v => v.Id == r.WorkflowVersionId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }, whenShared: Refuse<int>(", so it deletes nothing."));
    }

    private static Dictionary<Guid, CompilationKey> CurrentKeysOf(IReadOnlyCollection<CompilationKey> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var keys = new Dictionary<Guid, CompilationKey>();
        foreach (var key in current)
        {
            if (key is null)
                throw new ArgumentException("A current key is null.", nameof(current));
            if (string.IsNullOrWhiteSpace(key.DefinitionDigest) || string.IsNullOrWhiteSpace(key.DependencyHash))
                throw new ArgumentException(
                    $"The current key of version '{key.WorkflowVersionId}' has no digest or no hash.", nameof(current));
            if (!KeptExactly(key.DefinitionDigest) || !KeptExactly(key.DependencyHash))
                throw new ArgumentException(
                    $"The current key of version '{key.WorkflowVersionId}' holds a NUL or an unpaired surrogate, which "
                    + "no saved row does, so every old row of that version would differ from it.", nameof(current));
            if (!keys.TryAdd(key.WorkflowVersionId, key))
                throw new ArgumentException(
                    $"Version '{key.WorkflowVersionId}' is given two current keys.", nameof(current));
        }

        return keys;
    }

    private async Task<T> OnOwnContextAsync<T>(Func<WorkflowDbContext, Task<T>> work, Func<T> whenShared)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        if (db.Database.GetDbConnection().State != ConnectionState.Closed)
        {
            if (Interlocked.Exchange(ref sharedConnectionLogged, 1) == 0)
                logger.LogWarning(
                    "{Reason}. Verdicts are neither read nor saved here, so each is compiled again after a restart. "
                    + "To keep them, configure the workflow context with a connection string, not a DbConnection object.",
                    SharedConnection);
            return whenShared();
        }

        return await work(db).ConfigureAwait(false);
    }

    private static Func<T> Refuse<T>(string consequence) =>
        () => throw new CompilationStoreUnavailableException(SharedConnection + consequence);

    private static Task<WorkflowCompilation?> FindRowAsync(
        WorkflowDbContext db, CompilationVerdict verdict, CancellationToken cancellationToken) =>
        db.WorkflowCompilations.FirstOrDefaultAsync(r => r.WorkflowVersionId == verdict.WorkflowVersionId
            && r.DefinitionDigest == verdict.DefinitionDigest && r.DependencyHash == verdict.DependencyHash,
            cancellationToken);

    private static void Apply(WorkflowCompilation row, CompilationVerdict verdict)
    {
        row.IsValid = verdict.IsValid;
        row.DiagnosticsJson = ToJson(verdict.Diagnostics);
        row.BlockSourcesJson = ToJson(verdict.BlockSources);
        row.CompilerVersion = verdict.CompilerVersion;
        row.CompiledAtUtc = verdict.CompiledAtUtc;
    }

    private static WorkflowCompilation FromVerdict(CompilationVerdict verdict)
    {
        var row = new WorkflowCompilation
        {
            WorkflowVersionId = verdict.WorkflowVersionId,
            DefinitionDigest = verdict.DefinitionDigest,
            DependencyHash = verdict.DependencyHash,
        };
        Apply(row, verdict);
        return row;
    }

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, WorkflowGraphHelpers.JsonOptions);

    private static IEnumerable<(string Site, string? Text)> StringsOf(CompilationVerdict verdict)
    {
        yield return (nameof(verdict.DefinitionDigest), verdict.DefinitionDigest);
        yield return (nameof(verdict.DependencyHash), verdict.DependencyHash);
        yield return (nameof(verdict.CompilerVersion), verdict.CompilerVersion);
        for (var i = 0; i < verdict.BlockSources.Count; i++)
            yield return ($"BlockSources[{i}]", verdict.BlockSources[i]);

        for (var i = 0; i < verdict.Diagnostics.Count; i++)
        {
            var diagnostic = verdict.Diagnostics[i];
            yield return ($"Diagnostics[{i}].RuleId", diagnostic.RuleId);
            yield return ($"Diagnostics[{i}].Message", diagnostic.Message);
            yield return ($"Diagnostics[{i}].NodeId", diagnostic.NodeId);
            yield return ($"Diagnostics[{i}].PortName", diagnostic.PortName);
            yield return ($"Diagnostics[{i}].BlockSource", diagnostic.BlockSource);
            for (var j = 0; j < diagnostic.CanvasPath.Count; j++)
            {
                yield return ($"Diagnostics[{i}].CanvasPath[{j}].NodeId", diagnostic.CanvasPath[j].NodeId);
                yield return ($"Diagnostics[{i}].CanvasPath[{j}].BranchName", diagnostic.CanvasPath[j].BranchName);
            }
        }
    }

    private static bool KeptExactly(string? text)
    {
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done || rune.Value == 0)
                return false;
            rest = rest[used..];
        }

        return true;
    }

    private static CompilationVerdict ToVerdict(WorkflowCompilation row) => new(
        row.WorkflowVersionId,
        row.DefinitionDigest,
        row.DependencyHash,
        row.IsValid,
        JsonSerializer.Deserialize<IReadOnlyList<CompilationDiagnostic>>(row.DiagnosticsJson, WorkflowGraphHelpers.JsonOptions) ?? [],
        JsonSerializer.Deserialize<IReadOnlyList<string>>(row.BlockSourcesJson, WorkflowGraphHelpers.JsonOptions) ?? [],
        row.CompilerVersion,
        row.CompiledAtUtc);
}
