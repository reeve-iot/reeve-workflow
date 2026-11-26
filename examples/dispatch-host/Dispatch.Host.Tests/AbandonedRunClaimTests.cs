using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class AbandonedRunClaimTests(ClusterBackends backends)
{
    private static readonly TimeSpan Cutoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastTheCutoff = Cutoff + TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_row_written_a_moment_ago_is_not_abandoned()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_live");
        await desk.StartRunAsync(owner: "desk-a");

        (await desk.ClaimAsync()).Should().BeEmpty(
            "a live replica writes its row before every block, and reclaiming that is running it twice");
    }

    [Fact]
    public async Task A_row_nobody_has_touched_for_longer_than_the_cutoff_is_claimed_in_the_sweepers_name()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_stale");
        var executionId = await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff);

        var claimed = await desk.ClaimAsync(owner: "desk-surviving");

        claimed.Should().ContainSingle().Which.ExecutionId.Should().Be(executionId);
        claimed[0].OwnedByReplicaId.Should().Be("desk-surviving");
        claimed[0].Status.Should().Be(WorkflowRunStateStatus.Running,
            "the claim takes the row over where it stands; it does not close it and start again");
    }

    [Fact]
    public async Task A_row_one_sweep_took_is_not_there_for_the_next_one()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_twice");
        await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff);

        var first = await desk.ClaimAsync(owner: "desk-a");
        var second = await desk.ClaimAsync(owner: "desk-b");

        first.Should().ContainSingle();
        second.Should().BeEmpty("the claim renewed the row, so it is no longer old enough to be anybody's");
    }

    [Fact]
    public async Task A_claimed_row_carries_the_version_the_run_was_pinned_to()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_pin");
        var pinnedVersionId = Guid.NewGuid();
        await desk.StartRunAsync(
            ago: PastTheCutoff, pinnedVersionsJson: $"{{\"{desk.WorkflowId}\":\"{pinnedVersionId}\"}}");

        var claimed = (await desk.ClaimAsync()).Should().ContainSingle().Subject;

        WorkflowEngine.PinOf(claimed, desk.WorkflowId).VersionId.Should().Be(pinnedVersionId,
            "resuming needs the version the run was on, and reading the library's own table for it is what "
            + "a host had to write before this seam existed");
    }

    [Fact]
    public async Task A_claimed_row_says_when_its_run_started()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_started");
        var startedAgo = TimeSpan.FromHours(3);
        var executionId = await desk.StartRunAsync(ago: startedAgo);
        await desk.AgeAsync(executionId, PastTheCutoff);

        var claimed = (await desk.ClaimAsync()).Should().ContainSingle().Subject;

        claimed.StartedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow - startedAgo, TimeSpan.FromMinutes(1),
            "this run began three hours ago and the row has said so since the first migration");
        claimed.LastTransitionUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            "which is a different field, stamped by the claim itself — a sweep reading one for the other "
            + "would measure every bound from the moment it claimed");
    }

    [Fact]
    public async Task A_claimed_row_says_which_run_dispatched_it()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_child");
        var parent = Guid.NewGuid();
        var child = await desk.StartRunAsync(ago: PastTheCutoff, parentExecutionId: parent);

        var claimed = await desk.ClaimAsync();

        claimed.Should().ContainSingle().Which.ExecutionId.Should().Be(child,
            "the row is still taken — left behind it would stay Running for ever, blocking the workflow's "
            + "delete and ageing at the head of every later sweep");
        claimed[0].ParentExecutionId.Should().Be(parent,
            "and what the sweep decides from is this: a run a block started is ended rather than replayed, "
            + "because re-dispatching it as a root loses the inputs its parent wired in");
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Suspended)]
    [InlineData(WorkflowRunStateStatus.Waiting)]
    public async Task A_parked_run_is_never_claimed(WorkflowRunStateStatus parked)
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, $"claim_parked_{parked}".ToLowerInvariant());
        var executionId = await desk.StartRunAsync(ago: TimeSpan.FromDays(1));
        await desk.ParkAsync(executionId, parked, ago: TimeSpan.FromDays(1));

        (await desk.ClaimAsync()).Should().BeEmpty(
            "a parked run waits on a continuation of its own, and claiming it would run that twice");
    }

    [Fact]
    public async Task A_run_that_already_ended_is_never_claimed()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_ended");
        var executionId = await desk.StartRunAsync(ago: TimeSpan.FromDays(1));
        await desk.EndAsync(executionId, ago: TimeSpan.FromDays(1));

        (await desk.ClaimAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_sleeping_run_is_left_alone_until_it_is_due()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_sleeping");
        var executionId = await desk.StartRunAsync(ago: PastTheCutoff);
        await desk.ParkAsync(executionId, WorkflowRunStateStatus.Sleeping, ago: PastTheCutoff,
            wakeAt: DateTimeOffset.UtcNow + TimeSpan.FromHours(1));

        (await desk.ClaimAsync()).Should().BeEmpty("it is asleep, not abandoned");
    }

    [Fact]
    public async Task A_sleeping_run_past_its_wake_up_time_and_still_not_renewed_is_claimed()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_overslept");
        var executionId = await desk.StartRunAsync(ago: PastTheCutoff);
        await desk.ParkAsync(executionId, WorkflowRunStateStatus.Sleeping, ago: PastTheCutoff,
            wakeAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        (await desk.ClaimAsync()).Should().ContainSingle().Which.ExecutionId.Should().Be(executionId,
            "past its wake-up time and still not renewed, whoever parked it is gone");
    }

    [Fact]
    public async Task A_row_a_stopping_replica_handed_over_is_claimed_without_waiting_out_the_cutoff()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_handed_over");
        var executionId = await desk.StartRunAsync(owner: "desk-stopping");
        await desk.HandOverAsync(executionId, "desk-stopping");

        var claimed = await desk.ClaimAsync(owner: "desk-surviving");

        claimed.Should().ContainSingle().Which.ExecutionId.Should().Be(executionId,
            "a row that names no replica is one nobody is running, and a deploy should not be as slow as a crash");
        claimed[0].OwnedByReplicaId.Should().Be("desk-surviving");
        (await desk.ClaimAsync(owner: "desk-other")).Should().BeEmpty("the claim wrote its name on the row");
    }

    [Fact]
    public async Task A_claim_in_no_name_reads_a_handed_over_row_by_its_age_alone()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_handed_over_unnamed");
        var executionId = await desk.StartRunAsync(owner: "desk-stopping");
        await desk.HandOverAsync(executionId, "desk-stopping");

        (await desk.ClaimAsync(owner: null)).Should().BeEmpty(
            "on a host that names no replica every live row names nobody, so nobody is not a handover there");
    }

    [Fact]
    public async Task A_handed_over_sleep_is_left_alone_until_it_is_due()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_handed_over_sleep");
        var executionId = await desk.StartRunAsync(owner: "desk-stopping");
        await desk.ParkAsync(executionId, WorkflowRunStateStatus.Sleeping,
            wakeAt: DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
        await desk.HandOverAsync(executionId, owner: null);

        (await desk.ClaimAsync(owner: "desk-surviving")).Should().BeEmpty(
            "taken early, the sleep would be re-entered and slept out inside the sweep");
    }

    [Fact]
    public async Task A_sweep_takes_the_oldest_rows_first_and_no_more_than_it_asked_for()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_batch");
        var oldest = await desk.StartRunAsync(ago: PastTheCutoff + TimeSpan.FromMinutes(2));
        var middle = await desk.StartRunAsync(ago: PastTheCutoff + TimeSpan.FromMinutes(1));
        await desk.StartRunAsync(ago: PastTheCutoff);

        var claimed = await desk.ClaimAsync(batchSize: 2);

        claimed.Select(row => row.ExecutionId).Should().Equal(new[] { oldest, middle },
            "a replica coming up after an outage must not take a fleet's whole backlog, and the runs that "
            + "have waited longest are the ones to take first");
    }

    [Fact]
    public async Task Two_sweeps_at_the_same_moment_take_every_row_once_between_them()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_race");
        var abandoned = new List<Guid>();
        for (var i = 0; i < 12; i++)
            abandoned.Add(await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff));

        var first = desk.ClaimAsync(owner: "desk-a");
        var second = desk.ClaimAsync(owner: "desk-b");
        var claimed = (await Task.WhenAll(first, second)).SelectMany(rows => rows).ToList();

        claimed.Select(row => row.ExecutionId).Should().OnlyHaveUniqueItems(
            "a row two sweeps both see must be taken by exactly one of them, or the run is dispatched twice");
        claimed.Select(row => row.ExecutionId).Should().BeEquivalentTo(abandoned,
            "and between them they must leave none of it behind");
    }

    [Fact]
    public async Task A_renewed_claim_is_not_there_for_the_next_sweep()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_renew");
        var executionId = await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff);
        (await desk.ClaimAsync(owner: "desk-a")).Should().ContainSingle();

        await desk.AgeAsync(executionId, PastTheCutoff);
        (await desk.Recovery.RenewClaimAsync(executionId, "desk-a", CancellationToken.None))
            .Should().BeTrue();

        (await desk.ClaimAsync(owner: "desk-b")).Should().BeEmpty(
            "the sweep dispatches inline, so a run longer than the cutoff would otherwise be claimed by a "
            + "peer while the replica that took it is still running it");
    }

    [Fact]
    public async Task A_claim_somebody_else_holds_is_not_renewed()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_renew_lost");
        var executionId = await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff);
        (await desk.ClaimAsync(owner: "desk-a")).Should().ContainSingle();

        var renewed = await desk.Recovery.RenewClaimAsync(executionId, "desk-b", CancellationToken.None);

        renewed.Should().BeFalse(
            "a replica that lost the row must learn it here rather than write over the owner's timestamps");
    }

    [Fact]
    public async Task A_batch_is_claimed_by_one_statement_however_many_rows_it_takes()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_one_statement");
        var abandoned = new List<Guid>();
        for (var i = 0; i < 12; i++)
            abandoned.Add(await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff));

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(desk.ConnectionString, commands);
        IWorkflowRunRecoveryStore recovery = new EfWorkflowRunRecoveryStore(
            container.GetRequiredService<IServiceScopeFactory>());
        var before = await CommitsAsync(desk.ConnectionString);

        var claimed = await recovery.ClaimAbandonedRunsAsync(new WorkflowRunRecoveryClaim
        {
            AbandonedAfter = Cutoff, BatchSize = 20, ClaimedByReplicaId = "desk-surviving"
        }, CancellationToken.None);

        claimed.Select(row => row.ExecutionId).Should().BeEquivalentTo(abandoned);
        var spent = await CommitsAsync(desk.ConnectionString) - before;
        spent.Should().BeLessThan(claimed.Count,
            $"twelve rows were claimed and the clock and the claim are two statements between them; {spent} "
            + "commits over that window, this test's own reads included, is more than a batch taken at once "
            + "can account for");
        commands.All.Should().ContainSingle("Entity Framework runs the clock read and nothing else")
            .Which.Should().Contain("CURRENT_TIMESTAMP");
    }

    private static async Task<long> CommitsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_oldest_rows_come_back_first_even_when_the_table_holds_them_the_other_way_round()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_batch_order");
        var newest = await desk.StartRunAsync(ago: PastTheCutoff);
        var middle = await desk.StartRunAsync(ago: PastTheCutoff + TimeSpan.FromMinutes(1));
        var oldest = await desk.StartRunAsync(ago: PastTheCutoff + TimeSpan.FromMinutes(2));

        var claimed = await desk.ClaimAsync(batchSize: 3);

        claimed.Select(row => row.ExecutionId).Should().Equal([oldest, middle, newest],
            "a replica coming up after an outage takes the runs that have waited longest first");
    }

    [Fact]
    public async Task A_run_whose_replica_is_alive_is_not_taken_from_under_it()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_live_owner_beats");
        var live = await desk.StartRunAsync(owner: "desk-live", ago: PastTheCutoff);

        await using var holder = new NpgsqlConnection(desk.ConnectionString);
        await holder.OpenAsync();
        var beating = await holder.BeginTransactionAsync();
        await using (var beat = new NpgsqlCommand(
                         "UPDATE automation.workflow_executions_state "
                         + "SET last_transition_utc = CURRENT_TIMESTAMP, version = version + 1 "
                         + "WHERE execution_id = $1", holder, beating))
        {
            beat.Parameters.Add(new NpgsqlParameter { Value = live });
            (await beat.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var sweeping = desk.ClaimAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await beating.CommitAsync();

        (await sweeping.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeEmpty(
            "the replica that owns this run is alive and has just said so");
        (await desk.State.GetAsync(live, CancellationToken.None))!.OwnedByReplicaId.Should().Be("desk-live");
    }

    [Fact]
    public async Task A_row_another_writer_is_holding_is_stepped_over_rather_than_waited_for()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_skip_locked");
        var held = await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff + TimeSpan.FromMinutes(1));
        var free = await desk.StartRunAsync(owner: "desk-dying", ago: PastTheCutoff);

        await using var holder = new NpgsqlConnection(desk.ConnectionString);
        await holder.OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand(
                         "SELECT execution_id FROM automation.workflow_executions_state "
                         + "WHERE execution_id = $1 FOR UPDATE", holder, holding))
        {
            lockRow.Parameters.Add(new NpgsqlParameter { Value = held });
            await lockRow.ExecuteNonQueryAsync();
        }

        var claimed = await desk.ClaimAsync().WaitAsync(TimeSpan.FromSeconds(15));

        claimed.Select(row => row.ExecutionId).Should().Equal([free],
            "the older row is somebody's to write and this sweep steps over it; the other one is still taken");
        await holding.RollbackAsync();
    }

    [Fact]
    public async Task A_claimed_row_comes_back_with_everything_the_sweep_decides_from()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_whole_row");
        var pinnedVersionId = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var executionId = await desk.StartRunAsync(
            owner: "desk-dying", ago: PastTheCutoff, parentExecutionId: parent,
            pinnedVersionsJson: $"{{\"{desk.WorkflowId}\":\"{pinnedVersionId}\"}}");

        var claimed = (await desk.ClaimAsync()).Should().ContainSingle().Subject;

        claimed.ExecutionId.Should().Be(executionId);
        claimed.WorkflowId.Should().Be(desk.WorkflowId);
        claimed.WorkflowVersion.Should().Be(1);
        claimed.Status.Should().Be(WorkflowRunStateStatus.Running);
        claimed.VariablesJson.Should().Be("{}");
        claimed.ParentExecutionId.Should().Be(parent);
        WorkflowEngine.PinOf(claimed, desk.WorkflowId).VersionId.Should().Be(pinnedVersionId);
        claimed.OwnedByReplicaId.Should().Be("desk-surviving");
    }

    [Fact]
    public async Task A_finished_run_keeps_its_owner_and_is_still_never_claimed()
    {
        await using var desk = await ClaimDesk.OpenAsync(backends, "claim_finished_owner");
        var executionId = await desk.StartRunAsync(owner: "desk-dying", ago: TimeSpan.FromDays(1));
        await desk.EndAsync(executionId, ago: TimeSpan.FromDays(1));

        (await desk.State.GetAsync(executionId, CancellationToken.None))!.OwnedByReplicaId
            .Should().Be("desk-dying");
        (await desk.ClaimAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Completed)]
    [InlineData(WorkflowRunStateStatus.Failed)]
    [InlineData(WorkflowRunStateStatus.Cancelled)]
    public async Task A_claim_on_a_run_that_has_ended_is_not_renewed(WorkflowRunStateStatus terminal)
    {
        await using var desk = await ClaimDesk.OpenAsync(
            backends, $"claim_renew_ended_{terminal}".ToLowerInvariant());
        var executionId = await desk.StartRunAsync(owner: "desk-a");
        await desk.EndAsync(executionId, terminal: terminal);
        var ended = await desk.State.GetAsync(executionId, CancellationToken.None);

        var renewed = await desk.Recovery.RenewClaimAsync(executionId, "desk-a", CancellationToken.None);

        renewed.Should().BeFalse("the run is over; there is no claim left to keep fresh");
        var after = await desk.State.GetAsync(executionId, CancellationToken.None);
        after!.Version.Should().Be(ended!.Version, "a straggler heartbeat must not write a terminal row at all");
        after.LastTransitionUtc.Should().Be(ended.LastTransitionUtc);
    }

    private sealed class ClaimDesk : IAsyncDisposable
    {
        private ServiceProvider provider = null!;

        private SteppingClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public Guid WorkflowId { get; private set; }

        public string ConnectionString { get; private set; } = null!;
        public IWorkflowRunStateStore State { get; private set; } = null!;
        public IWorkflowRunRecoveryStore Recovery { get; private set; } = null!;

        public static async Task<ClaimDesk> OpenAsync(ClusterBackends backends, string databaseName)
        {
            var desk = new ClaimDesk();
            var connectionString = await backends.CreateDatabaseAsync(databaseName);
            desk.ConnectionString = connectionString;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(desk.Clock);
            services.AddWorkflows(workflows =>
            {
                workflows.ConfigureDbContext(context => context.UseNpgsql(connectionString));
            }).AddPostgreSqlSchema();

            desk.provider = services.BuildServiceProvider();
            desk.State = desk.provider.GetRequiredService<IWorkflowRunStateStore>();
            desk.Recovery = desk.provider.GetRequiredService<IWorkflowRunRecoveryStore>();

            using (var scope = desk.provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();
            }

            desk.WorkflowId = await FreightDesk.SeedAssignCarrierAsync(
                connectionString, ReeveWorkflow.Core.Model.WorkflowMode.Parallel, maxConcurrentRuns: 5,
                shipment: "SHP-CLAIM", carrier: "Ostbahn", dwellSeconds: 0);

            return desk;
        }

        public Task<Guid> StartRunAsync(
            string? owner = null, TimeSpan ago = default, string? pinnedVersionsJson = null,
            Guid? parentExecutionId = null) =>
            WrittenAsOf(ago, () => State.BeginAsync(new WorkflowRunStateBegin
            {
                WorkflowId = WorkflowId,
                WorkflowVersion = 1,
                VariablesJson = "{}",
                TriggerSource = "test",
                OwnedByReplicaId = owner,
                PinnedVersionsJson = pinnedVersionsJson,
                ParentExecutionId = parentExecutionId
            }, CancellationToken.None));

        public async Task ParkAsync(
            Guid executionId, WorkflowRunStateStatus status, TimeSpan ago = default, DateTimeOffset? wakeAt = null)
        {
            var moved = await WrittenAsOf(ago, () => State.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = executionId,
                FromStatus = WorkflowRunStateStatus.Running,
                ToStatus = status,
                CurrentBlockPath = FreightDesk.WorkNodeId,
                VariablesJson = "{}",
                WaitUntilUtc = wakeAt
            }, CancellationToken.None));
            moved.Should().BeTrue();
        }

        public async Task EndAsync(
            Guid executionId, TimeSpan ago = default,
            WorkflowRunStateStatus terminal = WorkflowRunStateStatus.Completed) =>
            (await WrittenAsOf(ago, () => State.CompleteAsync(
                executionId, terminal, null, CancellationToken.None)))
                .Should().BeTrue();

        public async Task AgeAsync(Guid executionId, TimeSpan ago)
        {
            var row = await State.GetAsync(executionId, CancellationToken.None);
            row.Should().NotBeNull();
            var moved = await WrittenAsOf(ago, () => State.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = executionId,
                FromStatus = row!.Status,
                ToStatus = row.Status,
                CurrentBlockPath = FreightDesk.WorkNodeId,
                VariablesJson = row.VariablesJson,
                OwnedByReplicaId = row.OwnedByReplicaId
            }, CancellationToken.None));
            moved.Should().BeTrue();
        }

        public async Task HandOverAsync(Guid executionId, string? owner)
        {
            var row = await State.GetAsync(executionId, CancellationToken.None);
            row.Should().NotBeNull();
            (await State.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = executionId,
                FromStatus = row!.Status,
                ToStatus = row.Status,
                CurrentBlockPath = row.CurrentBlockPath,
                VariablesJson = row.VariablesJson,
                WaitUntilUtc = row.WaitUntilUtc,
                OwnedByReplicaId = null,
                ExpectedOwnedByReplicaId = owner
            }, CancellationToken.None)).Should().BeTrue();
        }

        public async Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAsync(
            string? owner = "desk-surviving", int batchSize = 20) =>
            await Recovery.ClaimAbandonedRunsAsync(new WorkflowRunRecoveryClaim
            {
                AbandonedAfter = Cutoff,
                BatchSize = batchSize,
                ClaimedByReplicaId = owner
            }, CancellationToken.None);

        private async Task<T> WrittenAsOf<T>(TimeSpan ago, Func<Task<T>> write)
        {
            Clock.Set(DateTimeOffset.UtcNow - ago);
            try
            {
                return await write();
            }
            finally
            {
                Clock.Set(DateTimeOffset.UtcNow);
            }
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Set(DateTimeOffset to) => Interlocked.Exchange(ref ticks, to.UtcTicks);
    }
}
