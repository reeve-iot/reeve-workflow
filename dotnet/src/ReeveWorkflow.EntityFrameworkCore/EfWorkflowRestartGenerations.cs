using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The Restart generations every replica reads, in the workflow context's own two tables.</summary>
/// <remarks>Issuing is a compare-and-swap on the workflow's row, retried until it wins, so two
/// replicas never issue one generation. Liveness is written and judged on the database's clock, read with
/// <c>CURRENT_TIMESTAMP</c>, so replicas whose own clocks drifted apart agree on which trigger is still waiting. A
/// scope is opened per call, as the run-state store does.</remarks>
/// <param name="scopeFactory">Where each call's workflow context comes from.</param>
/// <param name="clock">A clock to judge liveness on instead of the database's, for a test that moves time by hand.
/// Null, as the host registration leaves it, reads the database's.</param>
public sealed class EfWorkflowRestartGenerations(IServiceScopeFactory scopeFactory, TimeProvider? clock = null)
    : IWorkflowRestartGenerations
{
    private static readonly TimeSpan ForgetLapsedAfter = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));


    /// <inheritdoc />
    public async Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        async Task<bool> PeerIssuedFirstAsync()
        {
            db.ChangeTracker.Clear();
            return await db.WorkflowRestartGenerations
                .AnyAsync(row => row.WorkflowId == workflowId, cancellationToken)
                .ConfigureAwait(false);
        }

        while (true)
        {
            var issued = await db.WorkflowRestartGenerations
                .AsNoTracking()
                .Where(row => row.WorkflowId == workflowId)
                .Select(row => (long?)row.Issued)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            long generation;
            if (issued is not { } last)
            {
                generation = 1;
                db.WorkflowRestartGenerations.Add(new WorkflowRestartGeneration(workflowId, generation, started: 0));
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    if (!await PeerIssuedFirstAsync().ConfigureAwait(false))
                        throw;
                    continue;
                }
                db.ChangeTracker.Clear();
            }
            else
            {
                generation = last + 1;
                var won = await db.WorkflowRestartGenerations
                    .Where(row => row.WorkflowId == workflowId && row.Issued == last)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Issued, generation), cancellationToken)
                    .ConfigureAwait(false);
                if (won == 0)
                    continue;
            }

            var now = await NowAsync(db, cancellationToken).ConfigureAwait(false);
            var forgetBefore = (now - ForgetLapsedAfter).ToUnixTimeMilliseconds();
            await db.WorkflowRestartWaiters
                .Where(row => row.WorkflowId == workflowId && row.LiveUntilUnixMs < forgetBefore)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            db.WorkflowRestartWaiters.Add(new WorkflowRestartWaiter(workflowId, generation, now + liveFor));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return generation;
        }
    }

    /// <inheritdoc />
    public async Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var liveUntil = (await NowAsync(db, cancellationToken).ConfigureAwait(false) + liveFor).ToUnixTimeMilliseconds();

        await db.WorkflowRestartWaiters
            .Where(row => row.WorkflowId == workflowId && row.Generation == generation)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LiveUntilUnixMs, liveUntil), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await db.WorkflowRestartWaiters
            .Where(row => row.WorkflowId == workflowId && row.Generation == generation)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The started mark rises before the waiter goes, and <see cref="ReadAsync"/> reads the waiters before the
    /// marks, so a reader in between sees the generation waiting, started, or both — never neither.</remarks>
    public async Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await db.WorkflowRestartGenerations
            .Where(row => row.WorkflowId == workflowId && row.Started < generation)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Started, generation), cancellationToken)
            .ConfigureAwait(false);

        await db.WorkflowRestartWaiters
            .Where(row => row.WorkflowId == workflowId && row.Generation == generation)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = (await NowAsync(db, cancellationToken).ConfigureAwait(false)).ToUnixTimeMilliseconds();

        var newestWaiting = await db.WorkflowRestartWaiters
            .AsNoTracking()
            .Where(row => row.WorkflowId == workflowId && row.LiveUntilUnixMs > now)
            .Select(row => (long?)row.Generation)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var marks = await db.WorkflowRestartGenerations
            .AsNoTracking()
            .Where(row => row.WorkflowId == workflowId)
            .Select(row => new { row.Issued, row.Started })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowRestartGenerationState(marks?.Issued ?? 0, marks?.Started ?? 0, newestWaiting);
    }

    private async Task<DateTimeOffset> NowAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        if (clock is not null)
            return clock.GetUtcNow();

        var rows = await db.Database
            .SqlQuery<DateTimeOffset>($"SELECT CURRENT_TIMESTAMP AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0].ToUniversalTime()
            : throw new InvalidOperationException(
                $"CURRENT_TIMESTAMP answered with {rows.Count} values rather than one, so no replica can tell which "
                + "Restart trigger is still waiting. Fill Seams.RestartGenerations with a store written for this "
                + "provider.");
    }
}
