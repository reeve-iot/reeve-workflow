using FluentAssertions;
using Dispatch.Host.Freight;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class HeldShipmentTests(ClusterBackends backends)
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task A_held_shipment_parks_the_run_and_the_bay_it_is_not_holding()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_parks");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-hold", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7001", DateTimeOffset.UtcNow + Hold);

        await desk.ExecuteAsync(workflowId);

        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 1,
            TimeSpan.FromSeconds(30),
            "the run to be parked in the library's own table, which this desk never wrote to",
            desk);

        parked[0].BlockId.Should().Be(FreightDesk.WorkNodeId);
        parked[0].Status.Should().Be(WorkflowSuspensionStatus.Suspended);
    }

    [Fact]
    public async Task The_hour_comes_and_the_run_walks_on()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_wakes");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-hold", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7002", DateTimeOffset.UtcNow + Hold);

        await desk.ExecuteAsync(workflowId);

        var resolved = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Any(row => row.Status != WorkflowSuspensionStatus.Suspended),
            TimeSpan.FromSeconds(60),
            "the waker to notice that the hour named in the hold has come",
            desk);

        resolved[0].Status.Should().Be(WorkflowSuspensionStatus.Resumed,
            "the template it parked on became true, which is not the same as running out of time");
        resolved[0].OutcomeJson.Should().Contain("satisfied");

        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
            TimeSpan.FromSeconds(60),
            "the run itself to end, rather than the suspension row alone being tidied",
            desk);
    }

    [Fact]
    public async Task A_hold_outlives_the_replica_that_started_it()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_survives");

        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));
        await FreightDesk.WarmAsync(postgres, dying, surviving);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7003", DateTimeOffset.UtcNow + Hold + Hold);

        await dying.ExecuteAsync(workflowId);

        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 1,
            TimeSpan.FromSeconds(30),
            "the dying replica to have parked the run before it is killed",
            dying, surviving);

        dying.Kill();

        var resolved = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Any(row => row.Status != WorkflowSuspensionStatus.Suspended),
            TimeSpan.FromSeconds(90),
            "the surviving replica to move a run it never started and was never told about",
            surviving);

        resolved[0].Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task A_wait_taken_by_a_replica_killed_before_it_re_entered_the_run_is_taken_over_by_its_peer()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_taker_dies");
        var cutoff = TimeSpan.FromSeconds(8);
        await using var taker = await DispatchReplica.StartAsync(Settings("desk-taker", postgres) with
        {
            StallAfterTakingAWait = true,
            AbandonedRunAfter = cutoff
        });
        await FreightDesk.WarmAsync(postgres, taker);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7006", DateTimeOffset.UtcNow + Hold);
        await taker.ExecuteAsync(workflowId);

        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Resumed,
            TimeSpan.FromSeconds(60),
            "the taker to take the wait once its hour came",
            taker);
        (await FreightDesk.RunStatesAsync(postgres, workflowId)).Should().ContainSingle()
            .Which.Status.Should().Be(nameof(WorkflowRunStateStatus.Suspended), "the taker never re-entered the run");

        taker.Kill();
        await using var peer = await DispatchReplica.StartAsync(Settings("desk-peer", postgres) with
        {
            AbandonedRunAfter = cutoff
        });

        var ended = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == nameof(WorkflowRunStateStatus.Completed),
            TimeSpan.FromSeconds(90),
            "the peer to take over a wait whose taker died, and walk the run on to its end",
            peer);

        ended[0].OwnedByReplicaId.Should().Be("desk-peer",
            "an ended row names the replica that ended it, and this run was finished by the peer "
            + "that took the wait over rather than by the replica that was killed holding it");
        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_wait_taken_over_from_a_dead_taker_whose_run_had_already_ended_closes_the_runs_entry_and_is_not_offered_again()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_taken_after_ending");
        var cutoff = TimeSpan.FromSeconds(8);
        await using var taker = await DispatchReplica.StartAsync(Settings("desk-taker-ended", postgres) with
        {
            StallAfterTakingAWait = true,
            AbandonedRunAfter = cutoff
        });
        await FreightDesk.WarmAsync(postgres, taker);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7007", DateTimeOffset.UtcNow + Hold);
        await taker.ExecuteAsync(workflowId);

        var taken = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Resumed,
            TimeSpan.FromSeconds(60),
            "the taker to take the wait once its hour came",
            taker);
        taker.Kill();

        var row = (await FreightDesk.RunStatesAsync(postgres, workflowId)).Should().ContainSingle().Subject;
        await FreightDesk.ExecuteSqlAsync(postgres,
            $"UPDATE automation.workflow_executions_state SET status = 'Completed', "
            + $"owned_by_replica_id = 'desk-some-sweep', "
            + $"failure_reason = '{WorkflowRunRecoverySweep.ReplayedReason}' WHERE execution_id = '{row.ExecutionId}'");

        await using var peer = await DispatchReplica.StartAsync(Settings("desk-peer-ended", postgres) with
        {
            AbandonedRunAfter = cutoff
        });

        var closed = await FreightDesk.EventuallyAsync(
            () => FreightDesk.JournalAsync(postgres, workflowId),
            entries => entries.Count == 1 && entries[0].Status != WorkflowExecutionStatus.Running,
            TimeSpan.FromSeconds(90),
            "the peer to take the abandoned wait over and close the entry of the run whose row had ended",
            peer);

        closed[0].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        closed[0].ErrorMessage.Should().StartWith(WorkflowRunResumer.RunStateEndedReason);
        (await FreightDesk.RunStatesAsync(postgres, workflowId)).Should().ContainSingle()
            .Which.Status.Should().Be(nameof(WorkflowRunStateStatus.Completed), "the resume walked nothing on");
        await using var container = FreightDesk.Container(postgres);
        var store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        (await store.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.Zero, 10, CancellationToken.None))
            .Candidates.Should().NotContain(candidate => candidate.SuspensionId == taken[0].Id,
                "a run that is over has no wait left to take over");
    }

    [Fact]
    public async Task A_page_of_holds_that_are_not_due_does_not_hide_the_ones_behind_it()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_paging");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-paging", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 10,
            shipment: "SHP-7005", DateTimeOffset.UtcNow.AddHours(6));

        for (var i = 0; i < 4; i++)
            await desk.ExecuteAsync(workflowId);

        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 4,
            TimeSpan.FromSeconds(30),
            "four holds to be parked",
            desk);

        await using var container = FreightDesk.Container(postgres);
        var store = new EfWorkflowSuspensionResumeStore(
            container.GetRequiredService<IServiceScopeFactory>(), []);

        var first = await store.ScanAsync(0, 2, default);
        var second = await store.ScanAsync(first.NextPosition, 2, default);
        var third = await store.ScanAsync(second.NextPosition, 2, default);

        first.NextPosition.Should().Be(2, "a full page says where the next one starts");
        first.Candidates.Concat(second.Candidates).Select(row => row.SuspensionId)
            .Should().BeEquivalentTo(parked.Select(row => row.Id),
                "two passes of two reach all four, which one repeated page never would");
        third.NextPosition.Should().Be(0, "a short page is the end of them, and the next starts over");
    }

    [Fact]
    public async Task A_page_read_on_after_the_rows_before_it_were_taken_does_not_step_over_the_rows_behind_them()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_keyset_drift");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-keyset", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 10,
            shipment: "SHP-7006", DateTimeOffset.UtcNow.AddHours(6));
        for (var i = 0; i < 4; i++)
            await desk.ExecuteAsync(workflowId);
        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 4,
            TimeSpan.FromSeconds(30),
            "four holds to be parked",
            desk);

        await using var container = FreightDesk.Container(postgres);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);

        var first = await store.ScanAsync(WorkflowSuspensionScanCursor.Start, 2, default);
        foreach (var taken in first.Candidates)
            (await store.TryResolveAsync(
                taken.SuspensionId, WorkflowSuspensionStatus.Cancelled, "{}", DateTimeOffset.UtcNow, default))
                .Should().NotBeNull();
        var second = await store.ScanAsync(first.NextCursor, 2, default);

        second.Candidates.Select(row => row.SuspensionId).Should().BeEquivalentTo(
            parked.Select(row => row.Id).Except(first.Candidates.Select(row => row.SuspensionId)),
            "the two behind the first page are still behind it");
    }

    [Fact]
    public async Task A_burst_of_holds_parked_in_one_instant_is_read_to_the_end_and_then_from_the_start()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_keyset_burst");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-burst", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 10,
            shipment: "SHP-7007", DateTimeOffset.UtcNow.AddHours(6));
        for (var i = 0; i < 4; i++)
            await desk.ExecuteAsync(workflowId);
        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 4,
            TimeSpan.FromSeconds(30),
            "four holds to be parked",
            desk);
        await FreightDesk.ExecuteSqlAsync(postgres,
            "UPDATE automation.workflow_suspensions SET suspended_at_utc = date_trunc('second', now()) - interval '1 minute'");

        await using var container = FreightDesk.Container(postgres);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);

        var cursor = WorkflowSuspensionScanCursor.Start;
        var pages = new List<List<Guid>>();
        for (var page = 0; page < 4; page++)
        {
            var scan = await store.ScanAsync(cursor, 2, default);
            pages.Add([.. scan.Candidates.Select(row => row.SuspensionId)]);
            cursor = scan.NextCursor;
        }

        pages[0].Concat(pages[1]).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(parked.Select(row => row.Id),
            "a cursor on the instant alone would read the first page of the four and step over the rest");
        pages[2].Should().BeEmpty("the page after the last row is short, which is the end");
        pages[3].Should().Equal(pages[0], "and the pass after it starts over");
    }

    [Fact]
    public async Task A_pass_the_due_rows_fill_hands_the_next_one_the_cursor_it_was_given()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_keyset_due");
        IReadOnlyList<ParkedRun> parked;
        await using (var desk = await DispatchReplica.StartAsync(Settings("desk-due", postgres)))
        {
            await FreightDesk.WarmAsync(postgres, desk);
            var workflowId = await FreightDesk.SeedHoldUntilAsync(
                postgres, WorkflowMode.Parallel, maxConcurrentRuns: 10,
                shipment: "SHP-7009", DateTimeOffset.UtcNow.AddHours(6));
            for (var i = 0; i < 4; i++)
                await desk.ExecuteAsync(workflowId);
            parked = await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows.Count == 4,
                TimeSpan.FromSeconds(30),
                "four holds to be parked",
                desk);
        }

        await FreightDesk.ExecuteSqlAsync(postgres, $"""
            UPDATE automation.workflow_suspensions SET timeout_at_utc = now() - interval '1 minute'
            WHERE id IN ('{parked[0].Id}', '{parked[1].Id}')
            """);

        await using var container = FreightDesk.Container(postgres);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        var reading = await store.ScanAsync(WorkflowSuspensionScanCursor.Start, 3, default);
        reading.Candidates.Should().HaveCount(3, "two due and the first of the two templates");

        var full = await store.ScanAsync(reading.NextCursor, 2, default);

        full.Candidates.Select(row => row.SuspensionId).Should().BeEquivalentTo([parked[0].Id, parked[1].Id]);
        full.NextCursor.Should().Be(reading.NextCursor);
    }

    [Fact]
    public async Task Two_replicas_looking_at_one_held_shipment_move_it_once()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hold_once");

        await using var first = await DispatchReplica.StartAsync(Settings("desk-a", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-b", postgres));
        await FreightDesk.WarmAsync(postgres, first, second);

        var workflowId = await FreightDesk.SeedHoldUntilAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7004", DateTimeOffset.UtcNow + Hold);

        await first.ExecuteAsync(workflowId);

        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Any(row => row.Status != WorkflowSuspensionStatus.Suspended),
            TimeSpan.FromSeconds(60),
            "one of the two replicas to take the parked run",
            first, second);

        await Task.Delay(TimeSpan.FromSeconds(10));

        (await FreightDesk.SuspensionsAsync(postgres, workflowId)).Should().ContainSingle();
        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle(
            "the run was re-entered once, by whichever of the two got the row");
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString
    };
}
