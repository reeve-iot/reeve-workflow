using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowStartAdmissionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_manual_start_asks_the_host_once_with_what_the_library_knows_and_runs_when_admitted()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host();
        using var engine = world.Engine(startAdmission: host);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        var asked = host.Asked.Should().ContainSingle().Subject;
        asked.Should().BeEquivalentTo(new WorkflowStartRequest(world.WorkflowId, world.Version.Id, WorkflowStartKind.Manual));
        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_manual_start_the_host_refuses_throws_its_code_and_message_and_leaves_no_run()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("workflow-disabled", "This workflow is disabled.") };
        using var engine = world.Engine(startAdmission: host);

        var refused = (await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which;

        (refused.WorkflowId, refused.Code, refused.Message, refused.IsFault)
            .Should().Be((world.WorkflowId, "workflow-disabled", "This workflow is disabled.", false));
        host.Asked.Should().ContainSingle();
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public async Task A_trigger_start_carries_the_fire_s_ids_and_a_refusal_stops_it()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host();
        using var engine = world.Engine(startAdmission: host);
        var fire = new WorkflowFire { AutomationId = Guid.NewGuid(), TriggerId = Guid.NewGuid() };

        await engine.ExecuteAsync(world.WorkflowId, fire, null).WaitAsync(Patience);

        host.Asked.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowStartRequest(world.WorkflowId, world.Version.Id, WorkflowStartKind.Trigger)
            {
                AutomationId = fire.AutomationId, TriggerId = fire.TriggerId
            });

        host.Decision = _ => WorkflowStartDecision.Refuse("quota", "No runs left.");
        await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, fire, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>().Where(e => e.Code == "quota");
        host.Asked.Should().HaveCount(2, "one question per start");
        world.HoldEntries.Should().Be(1, "only the admitted start ran");
    }

    [Fact]
    public async Task A_sub_workflow_start_names_its_parent_and_depth_and_a_refusal_stops_it()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("workflow-disabled", "Disabled.") };
        using var engine = world.Engine(startAdmission: host);
        var parent = Guid.NewGuid();

        var act = async () =>
        {
            using (WorkflowRunPinScope.Push(new WorkflowRunPin { CallDepth = 3 }))
                await engine.ExecuteAsync(world.WorkflowId, null, null, parentExecutionId: parent).WaitAsync(Patience);
        };

        await act.Should().ThrowAsync<WorkflowStartRefusedException>().Where(e => e.Code == "workflow-disabled");
        host.Asked.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowStartRequest(world.WorkflowId, world.Version.Id, WorkflowStartKind.SubWorkflow)
            {
                ParentExecutionId = parent, CallDepth = 3
            });
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public async Task With_no_host_admission_every_start_is_admitted()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_host_that_throws_has_not_admitted_and_its_fault_is_not_served()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var fault = new InvalidOperationException("connection string leaked");
        var host = new Host { Decision = _ => throw fault };
        using var engine = world.Engine(startAdmission: host);

        var refused = (await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which;

        refused.Code.Should().Be(WorkflowStartRefusalCodes.AdmissionFaulted);
        refused.IsFault.Should().BeTrue();
        refused.Message.Should().NotContain("leaked");
        refused.InnerException.Should().BeSameAs(fault);
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public async Task A_host_that_answers_nothing_has_not_admitted_either()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => null! };
        using var engine = world.Engine(startAdmission: host);

        (await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which.IsFault.Should().BeTrue();
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public async Task A_caller_that_gave_up_while_the_host_was_asked_is_cancelled_not_refused()
    {
        var world = new RunLifecycleWorld();
        using var caller = new CancellationTokenSource();
        var host = new Host { Decision = _ => { caller.Cancel(); throw new OperationCanceledException(caller.Token); } };
        using var engine = world.Engine(startAdmission: host);

        await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public async Task A_replay_of_an_abandoned_run_is_not_asked_even_when_the_host_would_refuse_everything()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };
        using var engine = world.Engine(startAdmission: host);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        (await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId).WaitAsync(Patience))
            .Should().Be(WorkflowReplayOutcome.Finished);

        host.Asked.Should().BeEmpty();
        world.HoldEntries.Should().Be(1, "the replay ran");
    }

    [Fact]
    public async Task A_resume_of_an_abandoned_run_is_not_asked()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };
        using var engine = world.Engine(startAdmission: host);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        (await engine.TryResumeAsync(abandoned, world.WorkflowId).WaitAsync(Patience)).Should().BeTrue();

        host.Asked.Should().BeEmpty();
        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_run_resumed_after_a_wait_is_not_asked()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };
        using var engine = world.Engine(startAdmission: host);

        (await world.Resumer().ResumeFromSuspensionAsync(world.Parked(), CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        host.Asked.Should().BeEmpty();
        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_run_the_recovery_sweep_re_enters_from_its_checkpoint_is_not_asked()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.SetResult();
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };
        var journal = Guid.NewGuid();
        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var row = world.RunState.Seed(
            world.WorkflowId, WorkflowRunStateStatus.Running, "replica-dead", RunLifecycleWorld.HoldNodeId);
        world.RunState.Checkpointed(row, WorkflowRunCheckpoint.Build(
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["start"] = new Dictionary<string, object?>(), ["tick"] = new Dictionary<string, object?>()
            },
            TestPrograms.Untyped, RunLifecycleWorld.HoldNodeId, journal, row, "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20));
        using var engine = world.Engine(startAdmission: host);
        var sweep = new WorkflowRunRecoverySweep(
            engine, world.RunState, new WorkflowRunReentryTests.ClaimsEverythingRunning(world.RunState),
            new WorkflowRunRecoveryOptions(), RunLifecycleWorld.ReplicaId, NullLogger.Instance, world.Resumer());

        await sweep.SweepAsync(CancellationToken.None);

        host.Asked.Should().BeEmpty();
        world.HoldEntries.Should().Be(1, "the block it died in ran again");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_run_that_retries_its_attempt_is_asked_once_for_the_run_not_once_per_attempt()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { FailHoldTimes = 1 };
        world.Release.SetResult();
        var host = new Host();
        using var engine = world.Engine(startAdmission: host);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(2, "the first attempt failed and the second ran");
        host.Asked.Should().ContainSingle();
    }

    [Fact]
    public async Task A_start_the_host_refuses_holds_no_place_in_a_single_workflow()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)), WorkflowMode.Single);
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };
        using var engine = world.Engine(startAdmission: host);

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>();
        host.Decision = _ => WorkflowStartDecision.Admit;
        await engine.ExecuteAsync(id, null, null).WaitAsync(Patience);

        world.Executed.Should().Equal("n");
        host.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task One_event_reaching_two_replicas_asks_the_host_once()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)));
        var host = new Host();
        var dedup = new ClaimsOnce();
        var request = new WorkflowAdmissionRequest(id)
        {
            FireDedupKey = "device:42/event:7", IsMultiReplica = true, StartKind = WorkflowStartKind.Trigger
        };

        foreach (var replica in new[] { Replica(world, dedup, host), Replica(world, dedup, host) })
        {
            var result = await replica.AdmitAsync(request, Coordinator, CancellationToken.None);
            result.Decision?.Lease?.Dispose();
        }

        host.Asked.Should().ContainSingle("the second replica's copy of the event was collapsed before it was a start");
    }

    [Fact]
    public async Task A_request_built_by_hand_without_a_start_kind_is_not_asked()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)));
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("no", "No.") };

        var result = await Replica(world, null, host).AdmitAsync(new WorkflowAdmissionRequest(id), Coordinator, CancellationToken.None);

        result.IsAdmitted.Should().BeTrue();
        result.Decision?.Lease?.Dispose();
        host.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_start_of_a_workflow_that_does_not_compile_is_the_host_s_refusal_and_writes_nothing()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)));
        world.Remove(AdmissionGateWorld.Leaf);
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("workflow-disabled", "This workflow is disabled.") };
        using var engine = world.Engine(startAdmission: host);

        for (var attempt = 0; attempt < 2; attempt++)
            await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
                .Should().ThrowAsync<WorkflowStartRefusedException>().Where(e => e.Code == "workflow-disabled");

        host.Asked.Should().HaveCount(2);
        world.Journal.Logs.Should().BeEmpty("a refused start compiles nothing and writes no did-not-compile row");

        host.Decision = _ => WorkflowStartDecision.Admit;
        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>("the workflow really does not compile once the host admits");
    }

    [Fact]
    public async Task A_start_the_host_admitted_may_still_be_refused_by_the_lease_so_an_admit_is_not_a_run()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)), WorkflowMode.Single);
        var host = new Host();
        var coordinator = new WorkflowRunCoordinator(id, WorkflowMode.Single, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var request = new WorkflowAdmissionRequest(id) { StartKind = WorkflowStartKind.Manual };

        var first = await Replica(world, null, host).AdmitAsync(request, _ => coordinator, CancellationToken.None);
        var second = await Replica(world, null, host).AdmitAsync(request, _ => coordinator, CancellationToken.None);

        first.IsAdmitted.Should().BeTrue();
        second.IsAdmitted.Should().BeFalse("the first run holds the only place");
        host.Asked.Should().HaveCount(2, "the host was asked about the start the lease then refused");
        first.Decision?.Lease?.Dispose();
    }

    [Fact]
    public async Task A_hook_fault_after_the_fire_claim_drops_that_fire_on_the_replica_that_would_have_run_it()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)));
        var dedup = new ClaimsOnce();
        var faulting = new Host { Decision = _ => throw new InvalidOperationException("the host's database is down") };
        var peer = new Host();
        var request = new WorkflowAdmissionRequest(id)
        {
            FireDedupKey = "device:42/event:7", IsMultiReplica = true, StartKind = WorkflowStartKind.Trigger
        };

        await FluentActions.Awaiting(() => Replica(world, dedup, faulting).AdmitAsync(request, Coordinator, CancellationToken.None))
            .Should().ThrowAsync<WorkflowStartRefusedException>().Where(e => e.IsFault);
        var copy = await Replica(world, dedup, peer).AdmitAsync(request, Coordinator, CancellationToken.None);

        (copy.IsAdmitted, copy.Outcome, copy.Reason).Should().Be((false, WorkflowAdmissionOutcomes.Dropped, "fire_dedup"));
        peer.Asked.Should().BeEmpty("the event is run nowhere: the same gap as a compile failure after the claim");
    }

    [Fact]
    public async Task A_refused_start_in_a_restart_workflow_supersedes_nothing()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)), WorkflowMode.Restart);
        var host = new Host();
        var coordinator = new WorkflowRunCoordinator(id, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var request = new WorkflowAdmissionRequest(id) { StartKind = WorkflowStartKind.Manual };
        var inFlight = (await Replica(world, null, host).AdmitAsync(request, _ => coordinator, CancellationToken.None)).Decision!.Lease!;

        host.Decision = _ => WorkflowStartDecision.Refuse("no", "No.");
        await FluentActions.Awaiting(() => Replica(world, null, host).AdmitAsync(request, _ => coordinator, CancellationToken.None))
            .Should().ThrowAsync<WorkflowStartRefusedException>();

        inFlight.Token.IsCancellationRequested.Should().BeFalse("only an admitted start supersedes");
        inFlight.Dispose();
    }

    [Fact]
    public async Task A_cancellation_from_the_host_s_own_token_is_a_fault_not_the_caller_giving_up()
    {
        var world = new RunLifecycleWorld();
        var host = new Host { Decision = _ => throw new OperationCanceledException() };
        using var engine = world.Engine(startAdmission: host);

        var refused = (await FluentActions.Awaiting(() => engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which;

        refused.IsFault.Should().BeTrue();
        AssertNothingOfTheRunExists(world);
    }

    [Fact]
    public void A_host_cannot_refuse_with_the_code_the_library_keeps_for_a_fault()
    {
        FluentActions.Invoking(() => WorkflowStartDecision.Refuse(WorkflowStartRefusalCodes.AdmissionFaulted, "Nope."))
            .Should().Throw<ArgumentException>().WithMessage("*library's own*");
    }

    [Fact]
    public async Task A_refused_start_of_a_workflow_whose_owner_may_not_script_is_the_host_s_refusal_and_writes_no_audit()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(AdmissionGateWorld.Graph(("n", AdmissionGateWorld.Leaf, null)));
        var host = new Host { Decision = _ => WorkflowStartDecision.Refuse("workflow-disabled", "This workflow is disabled.") };
        var audit = new RecordingAudit();
        var admission = new WorkflowRunAdmission(world.Store, new PrivilegedBlocks(world.Blocks), world.Compiler(), world.Journal,
            AlwaysRunningExecutionGate.Instance, new Nobody(), audit, NullWorkflowMetrics.Instance, NullLogger.Instance,
            startAdmission: host);
        var request = new WorkflowAdmissionRequest(id) { StartKind = WorkflowStartKind.Trigger };

        await FluentActions.Awaiting(() => admission.AdmitAsync(request, Coordinator, CancellationToken.None))
            .Should().ThrowAsync<WorkflowStartRefusedException>().Where(e => e.Code == "workflow-disabled");
        audit.Written.Should().BeEmpty("the host said no first: no ScriptingDenied event");

        host.Decision = _ => WorkflowStartDecision.Admit;
        await FluentActions.Awaiting(() => admission.AdmitAsync(request, Coordinator, CancellationToken.None))
            .Should().ThrowAsync<WorkflowScriptingPermissionException>("the scripting gate still judges an admitted start");
        audit.Written.Should().ContainSingle().Which.Kind.Should().Be(WorkflowAuditEventKinds.ScriptingDenied);
    }

    private static void AssertNothingOfTheRunExists(RunLifecycleWorld world)
    {
        world.HoldEntries.Should().Be(0, "no block ran");
        world.Journal.Logs.Should().BeEmpty("a refused start opens no entry");
        world.RunState.LastBegun.Should().BeNull("and no state row");
    }

    private static WorkflowRunAdmission Replica(AdmissionGateWorld world, IWorkflowFireDedup? dedup, IWorkflowStartAdmission host) =>
        new(world.Store, world.Blocks, world.Compiler(), world.Journal, AlwaysRunningExecutionGate.Instance, new Everyone(),
            new NoAudit(), NullWorkflowMetrics.Instance, NullLogger.Instance, dedup, startAdmission: host);

    private static WorkflowRunCoordinator Coordinator(Workflow workflow) =>
        new(workflow.Id, WorkflowMode.Parallel, 4, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);

    private sealed class Host : IWorkflowStartAdmission
    {
        private readonly ConcurrentQueue<WorkflowStartRequest> asked = new();

        public IReadOnlyCollection<WorkflowStartRequest> Asked => [.. asked];

        public Func<WorkflowStartRequest, WorkflowStartDecision> Decision { get; set; } = _ => WorkflowStartDecision.Admit;

        public ValueTask<WorkflowStartDecision> DecideAsync(WorkflowStartRequest request, CancellationToken cancellationToken)
        {
            asked.Enqueue(request);
            return ValueTask.FromResult(Decision(request));
        }
    }

    private sealed class ClaimsOnce : IWorkflowFireDedup
    {
        private readonly HashSet<string> claimed = [];

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
        {
            lock (claimed)
                return Task.FromResult(claimed.Add(fireKey));
        }

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class Everyone : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class Nobody : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        public ConcurrentQueue<WorkflowAuditEvent> Written { get; } = new();

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Written.Enqueue(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class PrivilegedBlocks(IWorkflowBlocks inner) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => inner.Find(blockTypeId);
        public bool ContainsPrivilegedBlock(string? definitionJson) => true;
        public bool ContainsPrivilegedBlock(IReadOnlySet<string> blockTypeIds) => true;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => inner.GetAll();
        public string Generation => inner.Generation;
    }

    private sealed class NoAudit : IWorkflowAuditSink
    {
        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
