using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class EfWorkflowRestartGenerationsTests : RestartGenerationsContract, IDisposable
{
    private readonly FirstIssueInterceptor interceptor = new();
    private readonly SqliteWorkflowDatabase database;

    public EfWorkflowRestartGenerationsTests() =>
        database = new SqliteWorkflowDatabase(onFile: true, context: options => options.AddInterceptors(interceptor));

    protected override IWorkflowRestartGenerations Store() =>
        new EfWorkflowRestartGenerations(database.Services.GetRequiredService<IServiceScopeFactory>(), Clock);

    [Fact]
    public async Task A_workflows_first_generation_issued_by_a_peer_in_between_makes_this_one_the_second()
    {
        var workflowId = Guid.NewGuid();
        interceptor.BeforeFirstIssue = context => context.Database.ExecuteSqlRaw(
            "INSERT INTO workflow_restart_generations (workflow_id, issued, started) VALUES ({0}, 1, 0)", workflowId);

        var generation = await Store().IssueAsync(workflowId, TimeSpan.FromSeconds(15), CancellationToken.None);

        generation.Should().Be(2);
    }

    [Fact]
    public async Task A_first_issue_that_fails_for_another_reason_says_so_rather_than_asking_for_ever()
    {
        interceptor.BeforeFirstIssue = _ => throw new DbUpdateException("The disk is full.");

        var issue = () => Store().IssueAsync(Guid.NewGuid(), TimeSpan.FromSeconds(15), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await issue.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task The_registered_store_judges_a_waiter_on_the_databases_clock_not_the_replicas()
    {
        using var skewed = new SqliteWorkflowDatabase(services => services.AddSingleton<TimeProvider>(
            new SkewedClock(TimeSpan.FromHours(1))));
        var store = skewed.Services.GetRequiredService<IWorkflowRestartGenerations>();
        var workflowId = Guid.NewGuid();
        await InsertWaiterAsync(skewed, workflowId, generation: 3, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15));

        (await store.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().Be(3);
    }

    [Fact]
    public async Task The_registered_store_keeps_a_waiter_live_by_the_databases_clock_not_the_replicas()
    {
        using var skewed = new SqliteWorkflowDatabase(services => services.AddSingleton<TimeProvider>(
            new SkewedClock(TimeSpan.FromHours(-1))));
        var store = skewed.Services.GetRequiredService<IWorkflowRestartGenerations>();
        var workflowId = Guid.NewGuid();
        var liveFor = TimeSpan.FromSeconds(15);

        var generation = await store.IssueAsync(workflowId, liveFor, CancellationToken.None);
        var issuedUntil = await LiveUntilAsync(skewed, workflowId, generation);
        await store.TouchAsync(workflowId, generation, liveFor, CancellationToken.None);
        var touchedUntil = await LiveUntilAsync(skewed, workflowId, generation);

        var now = DateTimeOffset.UtcNow;
        issuedUntil.Should().BeCloseTo(now + liveFor, TimeSpan.FromSeconds(5));
        touchedUntil.Should().BeCloseTo(now + liveFor, TimeSpan.FromSeconds(5));
    }

    private static async Task InsertWaiterAsync(
        SqliteWorkflowDatabase database, Guid workflowId, long generation, DateTimeOffset liveUntil)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.WorkflowRestartGenerations.Add(new WorkflowRestartGeneration(workflowId, generation, started: 0));
        db.WorkflowRestartWaiters.Add(new WorkflowRestartWaiter(workflowId, generation, liveUntil));
        await db.SaveChangesAsync();
    }

    private static async Task<DateTimeOffset> LiveUntilAsync(SqliteWorkflowDatabase database, Guid workflowId, long generation)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var waiter = await db.WorkflowRestartWaiters.AsNoTracking()
            .SingleAsync(row => row.WorkflowId == workflowId && row.Generation == generation);
        return DateTimeOffset.FromUnixTimeMilliseconds(waiter.LiveUntilUnixMs);
    }

    private sealed class SkewedClock(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + by;
    }

    public void Dispose() => database.Dispose();

    private sealed class FirstIssueInterceptor : SaveChangesInterceptor
    {
        public Action<DbContext> BeforeFirstIssue { set => BeforeFirstIssueField = value; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<WorkflowRestartGeneration>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref BeforeFirstIssueField, null) is { } arrange)
            {
                arrange(context);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private Action<DbContext>? BeforeFirstIssueField;
    }
}
