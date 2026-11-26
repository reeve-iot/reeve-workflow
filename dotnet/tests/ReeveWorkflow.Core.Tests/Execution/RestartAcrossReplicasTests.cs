using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RestartAcrossReplicasTests
{
    private const string Channel = "restart";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_newest_trigger_waits_for_a_lock_a_peer_holds_however_long_and_then_runs()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;

        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));
        trigger.IsCompleted.Should().BeFalse("nothing newer exists, so it waits for the lock rather than give up");

        cluster.Lock.HeldByPeer = false;
        var decision = await trigger.WaitAsync(Patience);

        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_holding_the_lock_gives_way_to_a_newer_generation_still_waiting_and_stands_aside_once_it_started()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var older = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 1);

        var newer = await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        cluster.Lock.HeldByPeer = false;
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked > 0));
        await Task.Delay(300);

        older.IsCompleted.Should().BeFalse("it gave the lock back for the newer generation and waits to hear its fate");
        cluster.Lock.HeldHere.Should().BeFalse("the lock is free for the newer trigger to take");

        await cluster.Generations.StartAsync(replica.WorkflowId, newer, CancellationToken.None);
        var decision = await older.WaitAsync(Patience);

        decision.Lease.Should().BeNull();
        decision.Outcome.Should().Be(WorkflowAdmissionOutcomes.Skipped);
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull("a trigger that stood aside waits no more");
    }

    [Fact]
    public async Task A_trigger_that_gave_way_runs_when_the_newer_generation_stops_being_live()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var older = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 1);
        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        cluster.Lock.HeldByPeer = false;
        await Task.Delay(300);
        older.IsCompleted.Should().BeFalse();

        cluster.Clock.Advance(LiveFor + TimeSpan.FromSeconds(1));
        var decision = await older.WaitAsync(Patience);

        decision.Lease.Should().NotBeNull("nothing newer is live, so this is the newest trigger left");
        (await cluster.ReadAsync(replica)).Started.Should().Be(1);
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_waiting_for_the_lock_stands_aside_once_a_newer_generation_started()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var older = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 1);

        var newer = await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        await cluster.Generations.StartAsync(replica.WorkflowId, newer, CancellationToken.None);

        var decision = await older.WaitAsync(Patience);
        decision.Lease.Should().BeNull();
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        cluster.Lock.HeldByPeer.Should().BeTrue("it never took the lock the newer run holds");
    }

    [Fact]
    public async Task A_trigger_that_takes_a_free_lock_after_a_newer_generation_started_stands_aside()
    {
        var cluster = new Cluster();
        var script = new ScriptedGenerations { Answer = new WorkflowRestartGenerationState(2, 2, null) };
        var replica = cluster.Replica(generations: script);

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);

        decision.Lease.Should().BeNull();
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        cluster.Lock.HeldHere.Should().BeFalse();
        script.Retired.Should().Equal(1);
    }

    [Fact]
    public async Task A_trigger_that_takes_a_free_lock_while_a_newer_generation_waits_gives_it_back_and_waits_for_it()
    {
        var cluster = new Cluster();
        var script = new ScriptedGenerations { Answer = new WorkflowRestartGenerationState(2, 0, 2) };
        var replica = cluster.Replica(generations: script);

        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(300);
        trigger.IsCompleted.Should().BeFalse();
        cluster.Lock.HeldHere.Should().BeFalse("the newer trigger needs it");

        script.Answer = new WorkflowRestartGenerationState(2, 0, null);
        var decision = await trigger.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull("the newer trigger never ran and is no longer waiting");
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_burst_here_runs_only_the_newest_trigger_though_the_store_cannot_be_read()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(generations: new FailingGenerations(cluster.Generations) { ReadFails = true });
        var first = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var firstMayFinish = new TaskCompletionSource();
        first.Token.Register(() => _ = Task.Run(async () => { await firstMayFinish.Task; await first.DisposeAsync(); }));

        var second = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 2);
        var third = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 3);
        firstMayFinish.SetResult();

        var secondDecision = await second.WaitAsync(Patience);
        secondDecision.Lease.Should().BeNull();
        var thirdDecision = await third.WaitAsync(Patience);
        thirdDecision.Lease.Should().NotBeNull();
        await thirdDecision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_running_run_is_stopped_when_a_newer_generation_waits_though_the_word_never_arrived()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var run = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(run.Token.IsCancellationRequested));
        await run.DisposeAsync();
    }

    [Fact]
    public async Task A_running_run_is_not_stopped_for_a_newer_generation_that_lapsed()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var run = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        await cluster.Generations.IssueAsync(replica.WorkflowId, TimeSpan.Zero, CancellationToken.None);

        await Task.Delay(replica.WatchEvery * 8);
        run.Token.IsCancellationRequested.Should().BeFalse();

        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(run.Token.IsCancellationRequested));
        await run.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_says_its_generation_on_the_signal()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None);

        replica.Signal.Published.Should().Contain(published => published.Channel == Channel
            && published.Id == replica.WorkflowId && published.Payload == "1");
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_word_naming_a_generation_stops_only_the_runs_older_than_it()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var run = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        await replica.Signal.DeliverAsync(Channel, replica.WorkflowId, "1");
        run.Token.IsCancellationRequested.Should().BeFalse("it is this run's own generation, said late");

        await replica.Signal.DeliverAsync(Channel, replica.WorkflowId, "2");
        run.Token.IsCancellationRequested.Should().BeTrue();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task A_word_that_names_no_generation_still_stops_every_run_here()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var run = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        await replica.Signal.DeliverAsync(Channel, replica.WorkflowId, null);

        run.Token.IsCancellationRequested.Should().BeTrue();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_waiting_for_the_lock_says_its_word_again()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;

        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(replica.Signal.Published.Count(sent => sent.Channel == Channel) >= 2));
        replica.Signal.Published.Where(sent => sent.Channel == Channel).Should().OnlyContain(sent => sent.Payload == "1");
        cluster.Lock.HeldByPeer = false;
        await (await trigger.WaitAsync(Patience)).Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_waiting_for_the_lock_keeps_its_generation_live()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).NewestWaiting == 1);

        cluster.Clock.Advance(LiveFor + TimeSpan.FromSeconds(1));
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();

        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).NewestWaiting == 1);
        cluster.Lock.HeldByPeer = false;
        await (await trigger.WaitAsync(Patience)).Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gives_up_stops_waiting_and_gives_its_permit_back()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(caller.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull("an older trigger must not wait for it");
        cluster.Lock.HeldByPeer = false;
        var next = await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);
        next.Lease.Should().NotBeNull();
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_started_metric_throws_leaves_no_generation_waiting_or_started()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(metrics: new ThrowOnStarted());

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        (await cluster.ReadAsync(replica)).Should().Be(new WorkflowRestartGenerationState(1, 0, null));
        cluster.Lock.HeldHere.Should().BeFalse();
    }

    [Fact]
    public async Task A_resume_under_restart_waits_while_a_newer_trigger_is_waiting()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var waiting = await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);

        (await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None))
            .Should().BeNull("the waiting trigger is newer than any run coming back");
        cluster.Lock.HeldHere.Should().BeFalse();

        await cluster.Generations.RetireAsync(replica.WorkflowId, waiting, CancellationToken.None);
        await using var resumed = await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None);
        resumed.Should().NotBeNull();
    }

    [Fact]
    public async Task A_resumed_run_under_restart_is_older_than_every_generation()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var resumed = (await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None))!;

        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(resumed.Token.IsCancellationRequested));
        await resumed.DisposeAsync();
    }

    [Fact]
    public async Task A_resumed_run_under_restart_is_not_stopped_for_the_trigger_that_started_the_run_before_it_parked()
    {
        var cluster = new Cluster();
        var generations = new FailingGenerations(cluster.Generations);
        var replica = cluster.Replica(generations: generations);
        await (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!.DisposeAsync();
        (await cluster.ReadAsync(replica)).Started.Should().Be(1);

        var readsBefore = generations.Reads;
        var resumed = (await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None))!;
        await WaitUntilAsync(() => Task.FromResult(generations.Reads >= readsBefore + 3));

        resumed.Token.IsCancellationRequested.Should().BeFalse("nothing started or waits after it");

        var newer = await cluster.Generations.IssueAsync(replica.WorkflowId, TimeSpan.Zero, CancellationToken.None);
        await cluster.Generations.StartAsync(replica.WorkflowId, newer, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(resumed.Token.IsCancellationRequested));
        await resumed.DisposeAsync();
    }

    [Fact]
    public async Task A_run_placed_while_the_store_could_not_issue_is_not_stopped_once_the_store_is_back()
    {
        var cluster = new Cluster();
        var generations = new FailingGenerations(cluster.Generations);
        var replica = cluster.Replica(generations: generations);
        await (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!.DisposeAsync();

        generations.IssueFails = true;
        var degraded = (await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience)).Lease!;
        generations.IssueFails = false;
        var readsBefore = generations.Reads;
        await WaitUntilAsync(() => Task.FromResult(generations.Reads >= readsBefore + 3));

        degraded.Token.IsCancellationRequested.Should().BeFalse("nothing started or waits after it");

        var newer = await cluster.Generations.IssueAsync(replica.WorkflowId, TimeSpan.Zero, CancellationToken.None);
        await cluster.Generations.StartAsync(replica.WorkflowId, newer, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(degraded.Token.IsCancellationRequested));
        await degraded.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_waiting_for_the_lock_starts_no_run_once_its_workflow_was_forgotten()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked > 0));

        replica.Coordinator.Dispose();
        await Task.Delay(300);
        cluster.Lock.HeldByPeer = false;

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        replica.Coordinator.ActiveRunCount.Should().Be(0);
        cluster.Lock.HeldHere.Should().BeFalse();
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();
    }

    [Fact]
    public async Task A_trigger_waiting_for_a_lock_a_peer_keeps_stops_waiting_once_its_workflow_was_forgotten()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked > 0));

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();
    }

    [Fact]
    public async Task A_trigger_giving_way_to_a_newer_one_stops_waiting_once_its_workflow_was_forgotten()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.HeldByPeer = true;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked > 0));
        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        cluster.Lock.HeldByPeer = false;
        await Task.Delay(300);

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        cluster.Lock.HeldHere.Should().BeFalse();
    }

    [Fact]
    public async Task A_trigger_whose_workflow_is_forgotten_as_it_is_granted_the_lock_starts_no_run()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.WhileGranting = replica.Coordinator.Dispose;

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(CancellationToken.None).WaitAsync(Patience))
            .Should().ThrowAsync<ObjectDisposedException>();

        replica.Coordinator.ActiveRunCount.Should().Be(0);
        cluster.Lock.HeldHere.Should().BeFalse("nothing runs to give it back later");
    }

    [Fact]
    public async Task A_resume_whose_workflow_is_forgotten_as_it_is_granted_the_lock_starts_no_run()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        cluster.Lock.WhileGranting = replica.Coordinator.Dispose;

        await replica.Coordinator.Awaiting(c => c.TryAcquireForResumeAsync(CancellationToken.None).WaitAsync(Patience))
            .Should().ThrowAsync<ObjectDisposedException>();

        replica.Coordinator.ActiveRunCount.Should().Be(0);
        cluster.Lock.HeldHere.Should().BeFalse("nothing runs to give it back later");
    }

    [Fact]
    public async Task A_resumed_run_under_restart_is_stopped_for_a_trigger_that_started_before_the_watch_first_looked()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var resumed = (await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None))!;

        var newer = await cluster.Generations.IssueAsync(replica.WorkflowId, TimeSpan.Zero, CancellationToken.None);
        await cluster.Generations.StartAsync(replica.WorkflowId, newer, CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(resumed.Token.IsCancellationRequested));
        await resumed.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gave_up_while_the_store_could_not_issue_stops_nothing()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(generations: new FailingGenerations(cluster.Generations) { IssueFails = true });
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(caller.Token).WaitAsync(Patience))
            .Should().ThrowAsync<OperationCanceledException>();

        replica.Signal.Published.Should().BeEmpty("a caller that gave up stops nothing, here or on the peers");
    }

    [Fact]
    public async Task A_trigger_waiting_for_the_permit_stops_waiting_once_its_workflow_was_forgotten()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        await Task.Delay(400);

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();
    }

    [Fact]
    public async Task A_trigger_on_the_old_path_waiting_for_the_permit_stops_waiting_once_its_workflow_was_forgotten()
    {
        var cluster = new Cluster();
        var generations = new FailingGenerations(cluster.Generations);
        var replica = cluster.Replica(generations: generations);
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        generations.IssueFails = true;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        await Task.Delay(200);

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gives_up_is_not_held_by_a_store_that_never_answers_the_issue()
    {
        var cluster = new Cluster();
        var hanging = new TaskCompletionSource<long>();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (_, _, _, _) => hanging.Task
        });
        using var caller = new CancellationTokenSource();
        var trigger = replica.Coordinator.AcquireAsync(caller.Token);
        await Task.Delay(200);

        await caller.CancelAsync();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        replica.Signal.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_generation_that_lands_after_its_caller_gave_up_is_retired()
    {
        var cluster = new Cluster();
        var landing = new TaskCompletionSource();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = async (inner, id, liveFor, _) =>
            {
                await landing.Task;
                return await inner.IssueAsync(id, liveFor, CancellationToken.None);
            }
        });
        using var caller = new CancellationTokenSource();
        var trigger = replica.Coordinator.AcquireAsync(caller.Token);
        await Task.Delay(100);
        await caller.CancelAsync();
        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        landing.SetResult();

        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).Issued == 1);
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).NewestWaiting is null);
    }

    [Fact]
    public async Task A_trigger_whose_workflow_is_forgotten_while_its_generation_is_issued_says_no_cancel()
    {
        var cluster = new Cluster();
        var release = new TaskCompletionSource();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = async (inner, id, liveFor, _) =>
            {
                await release.Task;
                return await inner.IssueAsync(id, liveFor, CancellationToken.None);
            }
        });
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);

        replica.Coordinator.Dispose();
        release.SetResult();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        replica.Signal.Published.Should().BeEmpty("a trigger forgotten while its generation is issued stops nothing on the peers");
        await WaitUntilAsync(async () => (await cluster.ReadAsync(replica)).NewestWaiting is null);
    }

    [Fact]
    public async Task A_trigger_whose_workflow_is_forgotten_as_its_generation_lands_says_no_cancel()
    {
        var cluster = new Cluster();
        Replica replica = null!;
        replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (inner, id, liveFor, _) =>
            {
                replica.Coordinator.Dispose();
                return inner.IssueAsync(id, liveFor, CancellationToken.None);
            }
        });

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(CancellationToken.None).WaitAsync(Patience))
            .Should().ThrowAsync<ObjectDisposedException>();

        replica.Signal.Published.Should().BeEmpty();
        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();
    }

    [Fact]
    public async Task A_promoted_waiter_that_could_get_no_generation_is_not_stopped_once_the_store_is_back()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var old = await cluster.Generations.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        await cluster.Generations.StartAsync(workflowId, old, CancellationToken.None);
        var store = new FailingGenerations(cluster.Generations) { IssueFails = true, ReadFails = true };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        var promoted = (await waiter.WaitAsync(Patience)).Lease!;

        store.IssueFails = false;
        store.ReadFails = false;
        await Task.Delay(500);

        promoted.Token.IsCancellationRequested.Should().BeFalse("nothing started or waits after it");
        await promoted.DisposeAsync();
    }

    [Fact]
    public async Task A_resumed_run_is_not_stopped_for_a_trigger_that_ran_and_let_go_before_it_was_placed()
    {
        var cluster = new Cluster();
        var once = 0;
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                if (Interlocked.Increment(ref once) == 1)
                {
                    var peer = await inner.IssueAsync(id, LiveFor, CancellationToken.None);
                    await inner.StartAsync(id, peer, CancellationToken.None);
                }
                return state;
            }
        });

        var resumed = (await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None))!;
        await Task.Delay(replica.WatchEvery * 8);

        resumed.Token.IsCancellationRequested.Should().BeFalse("that trigger had run and let go before this run was placed");
        await resumed.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gave_up_while_its_generation_was_issued_leaves_nothing_waiting()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(generations: new CancelledAfterCommit(cluster.Generations));
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(caller.Token).WaitAsync(Patience))
            .Should().ThrowAsync<OperationCanceledException>();

        (await cluster.ReadAsync(replica)).NewestWaiting.Should().BeNull();
        replica.Signal.Published.Should().BeEmpty("a caller that gave up stops nothing");
    }

    [Fact]
    public async Task A_store_that_cannot_issue_leaves_the_trigger_on_the_old_path_that_stops_every_run()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(generations: new FailingGenerations(cluster.Generations) { IssueFails = true });

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);

        decision.Lease.Should().NotBeNull("a store that is away is no reason to lose the trigger");
        replica.Signal.Published.Should().Contain(sent => sent.Channel == Channel && sent.Payload == null);
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_store_that_cannot_issue_does_not_wait_for_a_lock_a_peer_keeps()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(generations: new FailingGenerations(cluster.Generations) { IssueFails = true });
        cluster.Lock.HeldByPeer = true;

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);

        decision.Lease.Should().BeNull();
        decision.Reason.Should().Be("restart_mode_already_running_on_another_replica");
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_or_marked_is_no_reason_to_stand_aside()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica(
            generations: new FailingGenerations(cluster.Generations) { ReadFails = true, StartFails = true });

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);

        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Two_replicas_with_triggers_in_turn_end_with_the_newest_one_running_and_the_older_stopped()
    {
        var cluster = new Cluster();
        var one = cluster.Replica();
        var two = cluster.Replica(one.WorkflowId);

        var first = (await one.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var firstToken = first.Token;
        firstToken.Register(() => _ = Task.Run(async () => await first.DisposeAsync()));

        var second = await two.Coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);

        second.Lease.Should().NotBeNull();
        firstToken.IsCancellationRequested.Should().BeTrue();
        (await cluster.ReadAsync(one)).Started.Should().Be(2);
        await second.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_that_could_get_no_generation_does_not_stand_aside_for_a_run_that_started_before_it_queued()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var old = await cluster.Generations.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        await cluster.Generations.StartAsync(workflowId, old, CancellationToken.None);
        var store = new FailingGenerations(cluster.Generations) { IssueFails = true, ReadFails = true };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        var decision = await waiter.WaitAsync(Patience);
        decision.Reason.Should().BeNull("the only run that started, started before this waiter queued");
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_that_could_get_no_generation_does_not_stand_aside_on_a_replica_that_once_ran_a_restart_trigger()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new FailingGenerations(cluster.Generations);
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        await (await coordinator.AcquireAsync(CancellationToken.None)).Lease!.DisposeAsync();
        coordinator.Reconfigure(WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop);

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        store.IssueFails = true;
        store.ReadFails = true;
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        var decision = await waiter.WaitAsync(Patience);
        decision.Reason.Should().BeNull("the Restart run here started before this waiter queued, and the store says nothing");
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_that_could_get_no_generation_waits_out_a_peers_lock_without_standing_aside_for_an_older_start()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new FailingGenerations(cluster.Generations);
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        await (await coordinator.AcquireAsync(CancellationToken.None)).Lease!.DisposeAsync();
        coordinator.Reconfigure(WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop);

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        store.IssueFails = true;
        store.ReadFails = true;
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        cluster.Lock.HeldByPeer = true;
        await running.Lease!.DisposeAsync();

        var asked = cluster.Lock.Asked;
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked >= asked + 8));
        waiter.IsCompleted.Should().BeFalse("it waits for the peer's lock rather than standing aside for the start before it");
        cluster.Lock.HeldByPeer = false;

        var decision = await waiter.WaitAsync(Patience);
        decision.Reason.Should().BeNull();
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_that_queued_before_any_generation_is_stopped_for_a_trigger_that_started_before_the_watch_first_looked()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(200));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        var promoted = (await waiter.WaitAsync(Patience)).Lease!;

        var peer = await cluster.Generations.IssueAsync(workflowId, TimeSpan.Zero, CancellationToken.None);
        await cluster.Generations.StartAsync(workflowId, peer, CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(promoted.Token.IsCancellationRequested));
        await promoted.DisposeAsync();
    }

    [Fact]
    public async Task A_resume_is_not_started_for_a_trigger_that_began_waiting_before_it_held_the_lock()
    {
        var cluster = new Cluster();
        var once = 0;
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                if (Interlocked.Increment(ref once) == 1)
                    await inner.IssueAsync(id, LiveFor, CancellationToken.None);
                return state;
            }
        });

        var resumed = await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None);
        try
        {
            resumed.Should().BeNull("a trigger was waiting when the resume held the lock and read the store");
            cluster.Lock.HeldHere.Should().BeFalse("a resume that is not started lets the lock go");
        }
        finally
        {
            if (resumed is not null) await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_resume_whose_read_under_the_lock_does_not_answer_lets_go_once_its_caller_gives_up()
    {
        var cluster = new Cluster();
        var reads = 0;
        var hang = new TaskCompletionSource();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                    await hang.Task.WaitAsync(token);
                return await inner.ReadAsync(id, token);
            }
        });
        using var caller = new CancellationTokenSource();
        var resume = replica.Coordinator.TryAcquireForResumeAsync(caller.Token);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reads) >= 2));

        await caller.CancelAsync();

        try
        {
            await resume.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
            cluster.Lock.HeldHere.Should().BeFalse();
            var again = await replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None).WaitAsync(Patience);
            again.Should().NotBeNull("the permit came back as well");
            await again!.DisposeAsync();
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task A_resume_whose_read_under_the_lock_does_not_answer_lets_go_once_its_workflow_is_forgotten()
    {
        var cluster = new Cluster();
        var reads = 0;
        var hang = new TaskCompletionSource();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                    await hang.Task.WaitAsync(token);
                return await inner.ReadAsync(id, token);
            }
        });
        var resume = replica.Coordinator.TryAcquireForResumeAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reads) >= 2));

        replica.Coordinator.Dispose();

        try
        {
            await resume.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
            cluster.Lock.HeldHere.Should().BeFalse();
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task A_trigger_forgotten_while_it_waits_for_its_permit_says_its_cancel_no_more()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        await Task.Delay(400);

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        var said = replica.Signal.Published.Count;
        await Task.Delay(1500);
        replica.Signal.Published.Count.Should().Be(said);
    }

    [Fact]
    public async Task A_run_whose_cancellation_callback_throws_does_not_fail_the_trigger_that_stops_it()
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        running.Token.Register(() => throw new InvalidOperationException("a block's own cancellation callback"));

        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        await running.DisposeAsync();

        var decision = await trigger.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task An_issue_that_fails_after_its_caller_gave_up_is_observed()
    {
        const string late = "the issue failed after its caller gave up";
        const string dropped = "a faulted task nobody observed";
        var seen = new ConcurrentQueue<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Enqueue(inner.Message);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            DropAFaultedTask(dropped);
            await AbandonAnIssueThatFailsLateAsync(late);
            for (var i = 0; i < 40 && !seen.Contains(dropped); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }

            seen.Should().Contain(dropped, "setup: the detector sees a faulted task nobody observed");
            seen.Should().NotContain(late);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task A_run_the_watch_stops_whose_cancellation_callback_throws_is_observed()
    {
        const string thrown = "a block's cancellation callback, reached by the watch";
        const string dropped = "a faulted task nobody observed";
        var seen = new ConcurrentQueue<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Enqueue(inner.Message);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await LetTheWatchStopARunWhoseCallbackThrowsAsync(thrown);
            DropAFaultedTask(dropped);
            for (var i = 0; i < 40 && !(seen.Contains(dropped) && seen.Contains(thrown)); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }

            seen.Should().Contain(dropped, "setup: the detector sees a faulted task nobody observed");
            seen.Should().NotContain(thrown);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task LetTheWatchStopARunWhoseCallbackThrowsAsync(string message)
    {
        var cluster = new Cluster();
        var replica = cluster.Replica();
        var lease = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        lease.Token.Register(() => throw new InvalidOperationException(message));

        await cluster.Generations.IssueAsync(replica.WorkflowId, LiveFor, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(lease.Token.IsCancellationRequested));
        await Task.Delay(200);
        await lease.DisposeAsync();
        replica.Coordinator.Dispose();
    }

    [Fact]
    public async Task A_trigger_whose_read_under_the_lock_does_not_answer_lets_go_once_its_workflow_is_forgotten()
    {
        var cluster = new Cluster();
        var reads = 0;
        var hang = new TaskCompletionSource();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    await hang.Task.WaitAsync(token);
                return await inner.ReadAsync(id, token);
            }
        });

        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reads) >= 1));
        cluster.Lock.HeldHere.Should().BeTrue("setup: the trigger holds the lock while it reads");

        replica.Coordinator.Dispose();

        try
        {
            await trigger.Awaiting(t => t.WaitAsync(TimeSpan.FromSeconds(3))).Should().ThrowAsync<ObjectDisposedException>();
            cluster.Lock.HeldHere.Should().BeFalse();
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task A_promoted_waiter_whose_issue_committed_and_then_threw_does_not_stand_aside_for_the_generation_it_left()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new CommitsThenThrows(cluster.Generations) { ReadFails = true, IssueCommitsThenThrows = true };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        var decision = await waiter.WaitAsync(Patience);
        try
        {
            (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting
                .Should().Be(1, "setup: the only generation waiting is the one this waiter's failed issue committed");
            decision.Reason.Should().BeNull("no trigger but this waiter exists");
            decision.Lease.Should().NotBeNull();
            await Task.Delay(500);
            decision.Lease!.Token.IsCancellationRequested.Should().BeFalse("nor does its watch stop it for that generation");
        }
        finally
        {
            if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_trigger_whose_issue_committed_and_then_threw_is_not_stopped_by_the_generation_it_left()
    {
        var cluster = new Cluster();
        var store = new CommitsThenThrows(cluster.Generations) { IssueCommitsThenThrows = true };
        var replica = cluster.Replica(generations: store);

        var decision = await replica.Coordinator.AcquireAsync(CancellationToken.None);
        decision.Lease.Should().NotBeNull("setup: the store could not issue, yet the decision still holds a lease");
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            decision.Lease!.Token.IsCancellationRequested.Should().BeFalse("no trigger but this one exists");
        }
        finally
        {
            await decision.Lease!.DisposeAsync();
            replica.Coordinator.Dispose();
        }
    }

    [Fact]
    public async Task An_older_trigger_on_a_peer_does_not_run_after_a_promoted_waiter_that_queued_after_it()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var gateA = new GatedLock(cluster.Lock) { Closed = true };
        using var a = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: gateA, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var older = a.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 1);
        var gaveUp = await cluster.Generations.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        await cluster.Generations.RetireAsync(workflowId, gaveUp, CancellationToken.None);

        var running = await b.AcquireAsync(CancellationToken.None);
        var waiter = b.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        b.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        var promoted = await waiter.WaitAsync(Patience);
        promoted.Lease.Should().NotBeNull("setup: the waiter, newer than generation 1, runs");

        gateA.Closed = false;
        await Task.Delay(300);
        await promoted.Lease!.DisposeAsync();

        var decision = await older.WaitAsync(Patience);
        if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        decision.Lease.Should().BeNull("generation 1 was issued before the waiter queued, and the waiter's run started");
    }

    [Fact]
    public async Task A_promoted_waiter_whose_issue_failed_stands_aside_for_a_trigger_waiting_on_its_own_replica()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new FailingGenerations(cluster.Generations) { ReadFails = true };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        var trigger = coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 1);
        store.IssueFails = true;
        await running.Lease!.DisposeAsync();

        var promoted = await waiter.WaitAsync(Patience);
        if (promoted.Lease is not null) await promoted.Lease.DisposeAsync();
        promoted.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        var decision = await trigger.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gives_up_while_it_reads_after_a_failed_issue_stops_nothing()
    {
        var cluster = new Cluster();
        var issues = 0;
        var issueFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangReads = false;
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (inner, id, liveFor, token) =>
            {
                if (Interlocked.Increment(ref issues) == 1)
                    return inner.IssueAsync(id, liveFor, token);
                issueFailed.TrySetResult();
                return Task.FromException<long>(new InvalidOperationException("The store is away."));
            },
            Read = async (inner, id, token) =>
            {
                if (Volatile.Read(ref hangReads))
                    await Task.Delay(Timeout.Infinite, token);
                return await inner.ReadAsync(id, token);
            }
        });
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        Volatile.Write(ref hangReads, true);
        using var caller = new CancellationTokenSource();
        var trigger = replica.Coordinator.AcquireAsync(caller.Token);
        await issueFailed.Task.WaitAsync(Patience);
        await Task.Delay(150);

        await caller.CancelAsync();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        running.Token.IsCancellationRequested.Should().BeFalse();
        replica.Signal.Published.Should().NotContain(said => said.Payload == null);
        await running.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_forgotten_while_it_reads_after_a_failed_issue_says_no_cancel_to_the_peers()
    {
        var cluster = new Cluster();
        var issueFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (_, _, _, _) =>
            {
                issueFailed.TrySetResult();
                return Task.FromException<long>(new InvalidOperationException("The store is away."));
            },
            Read = async (inner, id, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return await inner.ReadAsync(id, token);
            }
        });
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await issueFailed.Task.WaitAsync(Patience);
        await Task.Delay(150);

        replica.Coordinator.Dispose();

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        replica.Signal.Published.Should().NotContain(said => said.Payload == null);
    }

    [Fact]
    public async Task The_trigger_a_promoted_waiter_queued_behind_does_not_run_after_it()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var gateA = new GatedLock(cluster.Lock) { Closed = true };
        using var a = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: gateA, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var older = a.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 1);
        var running = await b.AcquireAsync(CancellationToken.None);
        var waiter = b.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        b.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        var promoted = await waiter.WaitAsync(Patience);
        promoted.Lease.Should().NotBeNull("setup: the waiter, newer than generation 1, runs");

        gateA.Closed = false;
        await Task.Delay(300);
        await promoted.Lease!.DisposeAsync();

        var decision = await older.WaitAsync(Patience);
        if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        decision.Lease.Should().BeNull("generation 1 was issued before the waiter queued, and the waiter's run started");
    }

    [Fact]
    public async Task A_trigger_whose_start_mark_does_not_answer_lets_the_lock_go_once_its_workflow_is_forgotten()
    {
        var cluster = new Cluster();
        var marking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = cluster.Replica(generations: new HangingStart(cluster.Generations, marking, hang.Task));
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await marking.Task.WaitAsync(Patience);
        cluster.Lock.HeldHere.Should().BeTrue("setup: the trigger holds the lock while it marks its start");

        replica.Coordinator.Dispose();

        try
        {
            await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
            cluster.Lock.HeldHere.Should().BeFalse();
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task The_trigger_a_promoted_waiter_queued_behind_stands_aside_while_it_waits_for_the_lock_the_run_holds()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var gateA = new GatedLock(cluster.Lock) { Closed = true };
        using var a = RestartReplica(workflowId, gateA, cluster.Generations);
        using var b = QueuedReplica(workflowId, cluster.Lock, cluster.Generations);
        var older = a.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 1);
        var promoted = await PromoteBehindAsync(b);
        gateA.Closed = false;

        var decision = await older.WaitAsync(Patience);

        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        await promoted.DisposeAsync();
    }

    [Fact]
    public async Task The_trigger_a_promoted_waiter_queued_behind_stands_aside_while_it_waits_for_its_permit()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        using var a = RestartReplica(workflowId, locks: null, cluster.Generations);
        using var b = QueuedReplica(workflowId, cluster.Lock, cluster.Generations);
        var first = (await a.AcquireAsync(CancellationToken.None)).Lease!;
        var older = a.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 2);
        var promoted = await PromoteBehindAsync(b);

        var decision = await older.WaitAsync(Patience);

        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        await promoted.DisposeAsync();
        await first.DisposeAsync();
    }

    [Fact]
    public async Task The_trigger_a_promoted_waiter_queued_behind_stands_aside_on_what_it_reads_once_it_holds_the_lock()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var gateA = new GatedLock(cluster.Lock) { Closed = true };
        var stale = new HookedGenerations(cluster.Generations)
        {
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                return gateA.Holds ? state : state with { Started = 0 };
            }
        };
        using var a = RestartReplica(workflowId, gateA, stale);
        using var b = QueuedReplica(workflowId, cluster.Lock, cluster.Generations);
        var older = a.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting == 1);
        var promoted = await PromoteBehindAsync(b);
        await promoted.DisposeAsync();

        gateA.Closed = false;
        var decision = await older.WaitAsync(Patience);

        if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_trigger_a_promoted_waiter_on_its_own_replica_queued_behind_stands_aside_on_what_its_replica_knows(
        bool whileTheRunHoldsThePermit)
    {
        var workflowId = Guid.NewGuid();
        var store = new FailingGenerations(new InProcessRestartGenerations());
        var reads = 0;
        var counted = new HookedGenerations(store)
        {
            Read = (inner, id, token) =>
            {
                Interlocked.Increment(ref reads);
                return inner.ReadAsync(id, token);
            }
        };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: null, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: counted, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var trigger = coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(first.Token.IsCancellationRequested));
        coordinator.Reconfigure(WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop);
        var before = Volatile.Read(ref reads);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reads) >= before + 4));
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 5, WorkflowOverflowBehavior.Drop);
        store.ReadFails = true;
        await first.DisposeAsync();

        var promoted = (await waiter.WaitAsync(Patience)).Lease;
        promoted.Should().NotBeNull("setup: the waiter queued behind generation 2 and took the place first");
        if (!whileTheRunHoldsThePermit)
            await promoted!.DisposeAsync();

        var decision = await trigger.WaitAsync(Patience);

        if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        if (whileTheRunHoldsThePermit) await promoted!.DisposeAsync();
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
    }

    [Fact]
    public async Task A_trigger_whose_read_after_a_failed_issue_answers_while_its_workflow_is_being_forgotten_says_no_cancel()
    {
        var cluster = new Cluster();
        var issues = 0;
        var failed = 0;
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (inner, id, liveFor, token) =>
            {
                if (Interlocked.Increment(ref issues) == 1)
                    return inner.IssueAsync(id, liveFor, token);
                Volatile.Write(ref failed, 1);
                return Task.FromException<long>(new InvalidOperationException("The store is away."));
            },
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                if (Volatile.Read(ref failed) == 1)
                    await cancelling.Task;
                return state;
            }
        });
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        running.Token.Register(() => { cancelling.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); });
        var trigger = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);

        var disposing = Task.Run(() => replica.Coordinator.Dispose());
        await cancelling.Task.WaitAsync(Patience);
        await Task.Delay(300);
        release.Set();
        await disposing.WaitAsync(Patience);

        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        replica.Signal.Published.Should().NotContain(said => said.Payload == null);
    }

    [Fact]
    public async Task A_trigger_without_a_generation_starts_and_is_stopped_by_a_trigger_waiting_on_its_own_replica()
    {
        var cluster = new Cluster();
        var failing = new FailingGenerations(cluster.Generations);
        var inDegraded = new AsyncLocal<bool>();
        var degradedRead = 0;
        var readsAfterIt = 0;
        var replica = cluster.Replica(generations: new HookedGenerations(failing)
        {
            Read = (inner, id, token) =>
            {
                if (inDegraded.Value)
                    Volatile.Write(ref degradedRead, 1);
                else if (Volatile.Read(ref degradedRead) == 1)
                    Interlocked.Increment(ref readsAfterIt);
                return inner.ReadAsync(id, token);
            }
        });
        var first = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var waiting = replica.Coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(first.Token.IsCancellationRequested));
        failing.IssueFails = true;
        inDegraded.Value = true;
        var degraded = replica.Coordinator.AcquireAsync(CancellationToken.None);
        inDegraded.Value = false;
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref readsAfterIt) >= 3));
        await first.DisposeAsync();

        var decision = await degraded.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull("setup: the trigger without a generation took the place first");
        await WaitUntilAsync(() => Task.FromResult(decision.Lease!.Token.IsCancellationRequested));
        await decision.Lease!.DisposeAsync();

        var other = await waiting.WaitAsync(Patience);
        other.Lease.Should().NotBeNull("the trigger waiting here runs once the one it stopped let go");
        await other.Lease!.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_trigger_without_a_generation_is_stopped_by_a_trigger_still_waiting_on_a_peer_only_when_its_cancel_arrives(
        bool theCancelArrives)
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var failing = new FailingGenerations(cluster.Generations);
        var gateA = new GatedLock(cluster.Lock);
        var signalA = new RecordingSignal();
        var signalB = new RecordingSignal();
        using var a = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: gateA, slotGate: null, isMultiReplica: true, clusterSignal: signalA,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: signalB,
            restartChannel: Channel, restartGenerations: failing, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = (await b.AcquireAsync(CancellationToken.None)).Lease!;
        gateA.Closed = true;
        var waitingOnA = a.AcquireAsync(CancellationToken.None);
        using var pump = new CancellationTokenSource();
        var pumping = Task.Run(async () =>
        {
            var delivered = 0;
            while (!pump.IsCancellationRequested)
            {
                var said = signalA.Published;
                for (; delivered < said.Count; delivered++)
                    await signalB.DeliverAsync(said[delivered].Channel, said[delivered].Id, said[delivered].Payload);
                await Task.Delay(10);
            }
        });
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        (await cluster.Generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().Be(2);

        failing.IssueFails = true;
        if (!theCancelArrives)
        {
            pump.Cancel();
            await pumping;
        }
        var degraded = b.AcquireAsync(CancellationToken.None);
        await running.DisposeAsync();
        var decision = await degraded.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull("setup: the degraded trigger runs");

        var stopped = false;
        try
        {
            await Task.Delay(2500, decision.Lease!.Token);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        if (!pump.IsCancellationRequested) pump.Cancel();
        await pumping;
        await decision.Lease!.DisposeAsync();
        gateA.Closed = false;
        var other = await waitingOnA.WaitAsync(Patience);
        if (other.Lease is not null) await other.Lease.DisposeAsync();
        stopped.Should().Be(theCancelArrives);
    }

    [Fact]
    public async Task A_trigger_waiting_on_a_peer_whose_store_does_not_answer_its_touch_still_stops_a_trigger_without_a_generation()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var failing = new FailingGenerations(cluster.Generations);
        var hanging = new HangingTouch(cluster.Generations);
        var gateA = new GatedLock(cluster.Lock);
        var signalA = new RecordingSignal();
        var signalB = new RecordingSignal();
        using var a = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: gateA, slotGate: null, isMultiReplica: true, clusterSignal: signalA,
            restartChannel: Channel, restartGenerations: hanging, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: signalB,
            restartChannel: Channel, restartGenerations: failing, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = (await b.AcquireAsync(CancellationToken.None)).Lease!;
        gateA.Closed = true;
        var waitingOnA = a.AcquireAsync(CancellationToken.None);
        using var pump = new CancellationTokenSource();
        var pumping = Task.Run(async () =>
        {
            var delivered = 0;
            while (!pump.IsCancellationRequested)
            {
                var said = signalA.Published;
                for (; delivered < said.Count; delivered++)
                    await signalB.DeliverAsync(said[delivered].Channel, said[delivered].Id, said[delivered].Payload);
                await Task.Delay(10);
            }
        });
        await WaitUntilAsync(() => Task.FromResult(running.Token.IsCancellationRequested));
        await hanging.Touching.Task.WaitAsync(Patience);

        failing.IssueFails = true;
        var degraded = b.AcquireAsync(CancellationToken.None);
        await running.DisposeAsync();
        var decision = await degraded.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull("setup: the degraded trigger runs");

        var stopped = false;
        try
        {
            await Task.Delay(2500, decision.Lease!.Token);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        await Task.Delay(2500);

        pump.Cancel();
        await pumping;
        await decision.Lease!.DisposeAsync();
        gateA.Closed = false;
        var other = await waitingOnA.WaitAsync(Patience);
        if (other.Lease is not null) await other.Lease.DisposeAsync();
        stopped.Should().BeTrue("the waiting trigger says its cancel every second whether or not its store answers its touch");
        hanging.Touches.Should().Be(1, "a touch still going is not asked for again beside itself");
    }

    [Fact]
    public async Task A_waiting_trigger_whose_workflow_is_being_forgotten_says_its_cancel_no_more()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var signal = new SlowUnsubscribeSignal();
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: signal,
            restartChannel: Channel, restartGenerations: cluster.Generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var trigger = coordinator.AcquireAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(first.Token.IsCancellationRequested));

        var disposing = Task.Run(() => coordinator.Dispose());
        await signal.Unsubscribing.Task.WaitAsync(Patience);
        var saidBeforeForgotten = signal.Published.Count;
        signal.PublishedWhileUnsubscribing.Wait(TimeSpan.FromMilliseconds(2500));
        signal.Release.Set();
        await disposing.WaitAsync(Patience);
        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();

        signal.Published.Skip(saidBeforeForgotten).Should().BeEmpty("a trigger whose workflow is marked forgotten says no cancel");
        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_whose_caller_gives_up_as_its_read_after_a_failed_issue_returns_stops_nothing()
    {
        var cluster = new Cluster();
        var issues = 0;
        using var caller = new CancellationTokenSource();
        var failed = false;
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (inner, id, liveFor, token) =>
            {
                if (Interlocked.Increment(ref issues) == 1)
                    return inner.IssueAsync(id, liveFor, token);
                Volatile.Write(ref failed, true);
                return Task.FromException<long>(new InvalidOperationException("The store is away."));
            },
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                if (Volatile.Read(ref failed))
                    caller.Cancel();
                return state;
            }
        });
        var running = (await replica.Coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        await replica.Coordinator.Awaiting(c => c.AcquireAsync(caller.Token).WaitAsync(Patience))
            .Should().ThrowAsync<OperationCanceledException>();

        running.Token.IsCancellationRequested.Should().BeFalse();
        replica.Signal.Published.Should().NotContain(said => said.Payload == null);
        await running.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_whose_caller_gives_up_while_it_reads_after_a_failed_issue_gives_its_place_back()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new FailingGenerations(cluster.Generations) { ReadFails = true };
        var hang = false;
        var hooked = new HookedGenerations(store)
        {
            Read = async (inner, id, token) =>
            {
                if (Volatile.Read(ref hang))
                    await Task.Delay(Timeout.Infinite, token);
                return await inner.ReadAsync(id, token);
            }
        };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: hooked, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var waiter = coordinator.AcquireAsync(caller.Token);
        await Task.Delay(100);
        store.ReadFails = false;
        store.IssueFails = true;
        Volatile.Write(ref hang, true);
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        await Task.Delay(150);

        await caller.CancelAsync();
        await waiter.Awaiting(w => w.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        Volatile.Write(ref hang, false);
        store.IssueFails = false;
        var next = await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);
        next.Lease.Should().NotBeNull("the place the waiter held came back");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_promoted_waiter_whose_issue_committed_and_then_threw_waits_out_a_peers_lock_without_standing_aside_for_the_generation_it_left()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new CommitsThenThrows(cluster.Generations) { ReadFails = true };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: store, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        store.IssueCommitsThenThrows = true;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        cluster.Lock.HeldByPeer = true;
        await running.Lease!.DisposeAsync();

        var asked = cluster.Lock.Asked;
        await WaitUntilAsync(() => Task.FromResult(cluster.Lock.Asked >= asked + 8));
        waiter.IsCompleted.Should().BeFalse("the generation it left is nobody's newer trigger");
        cluster.Lock.HeldByPeer = false;

        var decision = await waiter.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    private static WorkflowRunCoordinator RestartReplica(
        Guid workflowId, IWorkflowRunLockProvider? locks, IWorkflowRestartGenerations generations) =>
        new(workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

    private static WorkflowRunCoordinator QueuedReplica(
        Guid workflowId, IWorkflowRunLockProvider locks, IWorkflowRestartGenerations generations) =>
        new(workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

    private static async Task<WorkflowRunLease> PromoteBehindAsync(WorkflowRunCoordinator queued)
    {
        var running = await queued.AcquireAsync(CancellationToken.None);
        var waiter = queued.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        queued.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();
        var promoted = await waiter.WaitAsync(Patience);
        promoted.Lease.Should().NotBeNull("setup: the waiter, newer than the generation it queued behind, runs");
        return promoted.Lease!;
    }

    [Fact]
    public async Task A_promoted_waiter_whose_issue_failed_still_stands_aside_for_a_trigger_issued_after_that()
    {
        var cluster = new Cluster();
        var workflowId = Guid.NewGuid();
        var store = new CommitsThenThrows(cluster.Generations) { ReadFails = true, IssueCommitsThenThrows = true };
        var reads = 0;
        var hooked = new HookedGenerations(store)
        {
            Read = async (inner, id, token) =>
            {
                var state = await inner.ReadAsync(id, token);
                if (Interlocked.Increment(ref reads) == 1)
                    await cluster.Generations.IssueAsync(id, LiveFor, CancellationToken.None);
                return state;
            }
        };
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: cluster.Lock, slotGate: null, isMultiReplica: true, clusterSignal: null,
            restartChannel: Channel, restartGenerations: hooked, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        store.ReadFails = false;
        coordinator.Reconfigure(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        var decision = await waiter.WaitAsync(Patience);
        if (decision.Lease is not null) await decision.Lease.DisposeAsync();
        decision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropAFaultedTask(string message)
    {
        var faulted = new TaskCompletionSource<long>();
        faulted.SetException(new InvalidOperationException(message));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonAnIssueThatFailsLateAsync(string message)
    {
        var cluster = new Cluster();
        var failing = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = cluster.Replica(generations: new HookedGenerations(cluster.Generations)
        {
            Issue = (_, _, _, _) => failing.Task
        });
        using var caller = new CancellationTokenSource();
        var trigger = replica.Coordinator.AcquireAsync(caller.Token);
        await Task.Delay(100);
        await caller.CancelAsync();
        await trigger.Awaiting(t => t.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        failing.SetException(new InvalidOperationException(message));
        await Task.Delay(100);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (!await condition())
            await Task.Delay(10, patience.Token);
    }

    private sealed class Cluster
    {
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        public ClusterLock Lock { get; } = new();
        public InProcessRestartGenerations Generations { get; }

        public Cluster() => Generations = new InProcessRestartGenerations(Clock);

        public Replica Replica(Guid? workflowId = null, IWorkflowMetrics? metrics = null, IWorkflowRestartGenerations? generations = null) =>
            new(workflowId ?? Guid.NewGuid(), Lock, generations ?? Generations, metrics ?? NullWorkflowMetrics.Instance);

        public Task<WorkflowRestartGenerationState> ReadAsync(Replica replica) =>
            Generations.ReadAsync(replica.WorkflowId, CancellationToken.None);
    }

    private sealed class Replica
    {
        public Replica(Guid workflowId, ClusterLock locks, IWorkflowRestartGenerations generations, IWorkflowMetrics metrics)
        {
            WorkflowId = workflowId;
            Coordinator = new WorkflowRunCoordinator(
                WorkflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
                logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: Signal,
                restartChannel: Channel, restartGenerations: generations, restartWatchInterval: WatchEvery);
        }

        public TimeSpan WatchEvery { get; } = TimeSpan.FromMilliseconds(50);
        public Guid WorkflowId { get; }
        public RecordingSignal Signal { get; } = new();
        public WorkflowRunCoordinator Coordinator { get; }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class ClusterLock : IWorkflowRunLockProvider
    {
        private string? heldBy;
        private int asked;

        public volatile bool HeldByPeer;

        public Action? WhileGranting { get; set; }

        public int Asked => asked;

        public bool HeldHere
        {
            get { lock (this) return heldBy is not null; }
        }

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref asked);
            lock (this)
            {
                if (HeldByPeer || heldBy is not null)
                    return Task.FromResult<IWorkflowRunLock?>(null);
                var held = new Held(this);
                heldBy = lockKey;
                WhileGranting?.Invoke();
                return Task.FromResult<IWorkflowRunLock?>(held);
            }
        }

        private sealed class Held(ClusterLock owner) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync()
            {
                lock (owner) owner.heldBy = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class GatedLock(IWorkflowRunLockProvider inner) : IWorkflowRunLockProvider
    {
        private int holds;

        public volatile bool Closed;

        public bool Holds => Volatile.Read(ref holds) > 0;

        public async Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            if (Closed || await inner.TryAcquireAsync(lockKey, cancellationToken) is not { } held)
                return null;
            Interlocked.Increment(ref holds);
            return new Counted(this, held);
        }

        private sealed class Counted(GatedLock gate, IWorkflowRunLock held) : IWorkflowRunLock
        {
            private int disposed;

            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => held.IsValidAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    Interlocked.Decrement(ref gate.holds);
                await held.DisposeAsync();
            }
        }
    }

    private sealed class CommitsThenThrows(IWorkflowRestartGenerations inner) : IWorkflowRestartGenerations
    {
        public volatile bool ReadFails;
        public volatile bool IssueCommitsThenThrows;

        public async Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken)
        {
            var generation = await inner.IssueAsync(workflowId, liveFor, CancellationToken.None);
            if (IssueCommitsThenThrows)
                throw new InvalidOperationException("The connection was lost after the commit.");
            return generation;
        }

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            ReadFails
                ? Task.FromException<WorkflowRestartGenerationState>(new InvalidOperationException("The store is away."))
                : inner.ReadAsync(workflowId, cancellationToken);
    }

    private sealed class HangingStart(IWorkflowRestartGenerations inner, TaskCompletionSource marking, Task hang)
        : IWorkflowRestartGenerations
    {
        public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.IssueAsync(workflowId, liveFor, cancellationToken);

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public async Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
        {
            marking.TrySetResult();
            await hang.WaitAsync(cancellationToken);
            await inner.StartAsync(workflowId, generation, cancellationToken);
        }

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.ReadAsync(workflowId, cancellationToken);
    }

    private sealed class FailingGenerations(IWorkflowRestartGenerations inner) : IWorkflowRestartGenerations
    {
        public bool IssueFails { get; set; }
        public bool ReadFails { get; set; }
        public bool StartFails { get; init; }

        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken) =>
            IssueFails ? throw new InvalidOperationException("The store is away.") : inner.IssueAsync(workflowId, liveFor, cancellationToken);

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            StartFails ? Task.FromException(new InvalidOperationException("The store is away.")) : inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return ReadFails ? Task.FromException<WorkflowRestartGenerationState>(new InvalidOperationException("The store is away.")) : inner.ReadAsync(workflowId, cancellationToken);
        }
    }

    private sealed class HookedGenerations(IWorkflowRestartGenerations inner) : IWorkflowRestartGenerations
    {
        public Func<IWorkflowRestartGenerations, Guid, TimeSpan, CancellationToken, Task<long>>? Issue { get; init; }

        public Func<IWorkflowRestartGenerations, Guid, CancellationToken, Task<WorkflowRestartGenerationState>>? Read { get; init; }

        public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken) =>
            Issue is null ? inner.IssueAsync(workflowId, liveFor, cancellationToken) : Issue(inner, workflowId, liveFor, cancellationToken);

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Read is null ? inner.ReadAsync(workflowId, cancellationToken) : Read(inner, workflowId, cancellationToken);
    }

    private sealed class CancelledAfterCommit(IWorkflowRestartGenerations inner) : IWorkflowRestartGenerations
    {
        public async Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken)
        {
            var generation = await inner.IssueAsync(workflowId, liveFor, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return generation;
        }

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.ReadAsync(workflowId, cancellationToken);
    }

    private sealed class ScriptedGenerations : IWorkflowRestartGenerations
    {
        private long issued;

        public volatile WorkflowRestartGenerationState Answer = new(0, 0, null);

        public List<long> Retired { get; } = [];

        public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref issued));

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
        {
            lock (Retired) Retired.Add(generation);
            return Task.CompletedTask;
        }

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(Answer);
    }

    private sealed class ThrowOnStarted : IWorkflowMetrics
    {
        public void RecordSupersession(Guid workflowId, string mode, string outcome)
        {
            if (outcome == WorkflowAdmissionOutcomes.Started)
                throw new InvalidOperationException("The metrics exporter is away.");
        }

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class HangingTouch(IWorkflowRestartGenerations inner) : IWorkflowRestartGenerations
    {
        private int touches;

        public TaskCompletionSource Touching { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Touches => Volatile.Read(ref touches);

        public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.IssueAsync(workflowId, liveFor, cancellationToken);

        public async Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref touches);
            Touching.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.ReadAsync(workflowId, cancellationToken);
    }

    private sealed class SlowUnsubscribeSignal : IWorkflowClusterSignal
    {
        private readonly List<string?> published = [];

        public TaskCompletionSource Unsubscribing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim PublishedWhileUnsubscribing { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public List<string?> Published
        {
            get { lock (published) return [.. published]; }
        }

        public Task PublishAsync(string channel, Guid id, string? payload, CancellationToken cancellationToken = default)
        {
            lock (published) published.Add(payload);
            if (Unsubscribing.Task.IsCompleted)
                PublishedWhileUnsubscribing.Set();
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler) =>
            new Subscription(this);

        private sealed class Subscription(SlowUnsubscribeSignal owner) : IDisposable
        {
            public void Dispose()
            {
                owner.Unsubscribing.TrySetResult();
                owner.Release.Wait(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class RecordingSignal : IWorkflowClusterSignal
    {
        private readonly List<(string Channel, Func<Guid, string?, CancellationToken, Task> Handler)> handlers = [];
        private readonly List<(string Channel, Guid Id, string? Payload)> published = [];

        public List<(string Channel, Guid Id, string? Payload)> Published
        {
            get { lock (published) return [.. published]; }
        }

        public Task PublishAsync(string channel, Guid id, string? payload, CancellationToken cancellationToken = default)
        {
            lock (published) published.Add((channel, id, payload));
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
        {
            lock (handlers) handlers.Add((channel, handler));
            return new Subscription();
        }

        public async Task DeliverAsync(string channel, Guid id, string? payload)
        {
            Func<Guid, string?, CancellationToken, Task>[] snapshot;
            lock (handlers) snapshot = [.. handlers.Where(subscribed => subscribed.Channel == channel).Select(subscribed => subscribed.Handler)];
            foreach (var handler in snapshot)
                await handler(id, payload, CancellationToken.None);
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
