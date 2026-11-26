using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunAdmissionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.NewGuid();

    [Fact]
    public async Task AWorkflowWhoseOwnerMayNotScriptIsRefusedAndAudited()
    {
        var world = new World { OwnerHasScripting = false,
            Blocks = new RegisteredWorkflowBlocks([new StubFactory("Script", privileged: true)]),
            DefinitionJson = "{\"nodes\":[{\"id\":\"input\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"x\",\"blockTypeId\":\"Script\",\"position\":{\"x\":1,\"y\":0},\"configuration\":{}}],\"edges\":[]}" };

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<WorkflowScriptingPermissionException>(
            "a privileged block is more dangerous than its inputs suggest, so the run must not start");
        world.Audit.Written.Should().ContainSingle()
            .Which.Kind.Should().Be(WorkflowAuditEventKinds.ScriptingDenied);
        world.Coordinators.Should().ContainSingle().Which.ActiveRunCount.Should().Be(0,
            "the coordinator is asked its mode for the fire claim before the gate, but the refusal takes none of its budget");
    }

    [Fact]
    public async Task A_privileged_block_behind_a_second_casing_of_nodes_is_refused_through_the_registered_blocks()
    {
        var world = new World
        {
            OwnerHasScripting = false,
            Blocks = new RegisteredWorkflowBlocks([new StubFactory("Log"), new StubFactory("Script", privileged: true)]),
            DefinitionJson = """{"nodes":[{"id":"a","blockTypeId":"Log"}],"Nodes":[{"id":"b","blockTypeId":"Script"}],"edges":[]}""",
        };

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<WorkflowScriptingPermissionException>(
            "the engine deserialises the second casing, so the gate must see the block behind it");
    }

    [Fact]
    public async Task A_cached_block_is_checked_again_after_live_registration_as_privileged()
    {
        var registry = new MutablePrivilegeRegistry();
        var world = new World
        {
            OwnerHasScripting = true,
            Blocks = registry,
            DefinitionJson = "{\"nodes\":[{\"id\":\"x\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}",
        };

        Func<Task<WorkflowAdmissionResult>> admit = () => world.AdmitAsync();
        var first = await admit();
        first.IsAdmitted.Should().BeTrue();
        first.Decision!.Lease!.Dispose();
        registry.Privileged = true;
        world.OwnerHasScripting = false;

        for (var i = 0; i < 2; i++)
            await admit.Should().ThrowAsync<WorkflowScriptingPermissionException>();

        world.Audit.Written.Where(item => item.Kind == WorkflowAuditEventKinds.ScriptingDenied)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unregistered_block_does_not_cause_a_scripting_refusal()
    {
        var world = new World { OwnerHasScripting = false,
            DefinitionJson = "{\"nodes\":[{\"id\":\"x\",\"blockTypeId\":\"Unknown.Block\"}],\"edges\":[]}" };

        var act = () => world.AdmitAsync();

        await act.Should().NotThrowAsync<WorkflowScriptingPermissionException>();
    }

    [Fact]
    public async Task A_privileged_id_is_refused_even_when_the_definition_cannot_bind()
    {
        var world = new World { OwnerHasScripting = false,
            Blocks = new RegisteredWorkflowBlocks([new StubFactory("Script", privileged: true)]),
            DefinitionJson = "{\"nodes\":[{\"id\":\"x\",\"blockTypeId\":\"Script\",\"position\":\"invalid\"}],\"edges\":[]}" };

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<WorkflowScriptingPermissionException>();
    }

    [Fact]
    public async Task A_cached_unprivileged_block_is_checked_again_after_becoming_privileged()
    {
        var registry = new MutablePrivilegeRegistry();
        var world = new World { OwnerHasScripting = false, Blocks = registry,
            DefinitionJson = "{\"nodes\":[{\"id\":\"__workflow_input__\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}" };

        var first = await world.AdmitAsync();
        first.Decision!.Lease!.Dispose();
        world.Authorization.AskedAbout.Should().BeEmpty();

        registry.Privileged = true;
        Func<Task<WorkflowAdmissionResult>> next = () => world.AdmitAsync();
        await next.Should().ThrowAsync<WorkflowScriptingPermissionException>();
        world.Authorization.AskedAbout.Should().ContainSingle().Which.Should().Be(OwnerId);
    }

    [Fact]
    public async Task A_cached_privileged_block_is_checked_again_after_becoming_unprivileged()
    {
        var registry = new MutablePrivilegeRegistry { Privileged = true };
        var world = new World { OwnerHasScripting = true, Blocks = registry,
            DefinitionJson = "{\"nodes\":[{\"id\":\"__workflow_input__\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}" };

        var first = await world.AdmitAsync();
        first.Decision!.Lease!.Dispose();
        world.Authorization.AskedAbout.Should().ContainSingle();

        registry.Privileged = false;
        var second = await world.AdmitAsync();
        second.IsAdmitted.Should().BeTrue();
        world.Authorization.AskedAbout.Should().ContainSingle();
        second.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task ThePrivilegedGateAsksAboutTheOwnerNotTheTrigger()
    {
        var world = new World { OwnerHasScripting = true,
            Blocks = new RegisteredWorkflowBlocks([new StubFactory("Script", privileged: true)]),
            DefinitionJson = "{\"nodes\":[{\"id\":\"input\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"x\",\"blockTypeId\":\"Script\",\"position\":{\"x\":1,\"y\":0},\"configuration\":{}}],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"input\",\"targetNodeId\":\"x\",\"mappings\":{}}]}" };

        var result = await world.AdmitAsync();
        result.IsAdmitted.Should().BeTrue();
        result.Decision!.Lease!.Dispose();
        world.Authorization.AskedAbout.Should().ContainSingle().Which.Should().Be(OwnerId,
            "a shared workflow would launder the permission if the triggering user were asked instead");
    }

    [Fact]
    public async Task AnUnprivilegedDefinitionNeverAsksTheAuthorizationSeamAtAll()
    {
        var world = new World { OwnerHasScripting = false, DefinitionIsPrivileged = false };

        var result = await world.AdmitAsync();

        result.IsAdmitted.Should().BeTrue();
        world.Authorization.AskedAbout.Should().BeEmpty(
            "the capability check costs a query, so an ordinary workflow must not pay for it");
        result.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task APinThatCannotBeHonouredFailsTheRunInsteadOfFallingBackToCurrent()
    {
        var world = new World();
        var missing = Guid.NewGuid();

        var act = () => world.AdmitAsync(r => r with { PinnedVersionId = missing });

        (await act.Should().ThrowAsync<PinnedWorkflowVersionNotFoundException>(
            "silently running current would run a version the caller did not ask for"))
            .Which.VersionId.Should().Be(missing);
    }

    [Fact]
    public async Task AFireAlreadyClaimedByAPeerIsDroppedBeforeTheLease()
    {
        var world = new World { FireAlreadyClaimed = true };

        var result = await world.AdmitAsync(r => r with { FireDedupKey = "k", IsMultiReplica = true });

        result.IsAdmitted.Should().BeFalse();
        result.Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped);
        world.Coordinators.Should().ContainSingle()
            .Which.ActiveRunCount.Should().Be(0,
                "the coordinator has to exist to be asked its mode, but a collapsed fire must take none of its budget");
    }

    [Fact]
    public async Task A_fire_key_naming_only_the_event_collapses_two_workflows_that_event_fired_into_one()
    {
        var dedup = new ClaimOnceFireDedup();
        var eventOnly = (WorkflowAdmissionRequest r) => r with { FireDedupKey = "device:42/event:7", IsMultiReplica = true };

        var first = await new World { DedupInstead = dedup }.AdmitAsync(eventOnly);
        var second = await new World { DedupInstead = dedup }.AdmitAsync(eventOnly);

        first.IsAdmitted.Should().BeTrue();
        second.IsAdmitted.Should().BeFalse("one key is one claim, whichever workflow presents it");
        second.Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped);
        second.Reason.Should().Be("fire_dedup");
        first.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task A_fire_key_the_caller_scoped_to_its_workflow_admits_each_workflow_once_across_replicas()
    {
        var dedup = new ClaimOnceFireDedup();
        var lights = new World { DedupInstead = dedup };
        var alarm = new World { DedupInstead = dedup };
        Func<WorkflowAdmissionRequest, WorkflowAdmissionRequest> ScopedTo(World world) =>
            r => r with { FireDedupKey = $"{world.WorkflowId}/device:42/event:7", IsMultiReplica = true };

        var lightsRun = await lights.AdmitAsync(ScopedTo(lights));
        var alarmRun = await alarm.AdmitAsync(ScopedTo(alarm));
        var lightsOnSibling = await lights.AdmitAsync(ScopedTo(lights));

        lightsRun.IsAdmitted.Should().BeTrue();
        alarmRun.IsAdmitted.Should().BeTrue("a second workflow the same event fired holds a claim of its own");
        lightsOnSibling.Reason.Should().Be("fire_dedup",
            "a sibling replica derives the same scoped key for the same fire, so the fan-out still collapses");
        dedup.Claimed.Should().HaveCount(2);
        lightsRun.Decision!.Lease!.Dispose();
        alarmRun.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task OnOneReplicaTheFireDedupGateIsNotConsulted()
    {
        var world = new World { FireAlreadyClaimed = true };

        var result = await world.AdmitAsync(r => r with { FireDedupKey = "k", IsMultiReplica = false });

        result.IsAdmitted.Should().BeTrue(
            "there is no fan-out to collapse on one replica, and claiming a key would only cost a round trip");
        world.FireDedup.Claims.Should().BeEmpty();
        result.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task ASuspendedEngineRefusesWithoutReadingTheStore()
    {
        var world = new World { ExecutionSuspended = true };

        var result = await world.AdmitAsync();

        result.IsAdmitted.Should().BeFalse();
        result.Outcome.Should().Be(WorkflowAdmissionOutcomes.Skipped);
        world.Store.Loads.Should().BeEmpty(
            "safe mode is active exactly when storage is the problem, so admission must not touch it");
    }

    [Fact]
    public async Task AWorkflowWithNoPublishedVersionIsAValidationFailureNotACrash()
    {
        var world = new World { Published = false };

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<WorkflowValidationException>();
    }

    [Fact]
    public async Task AWorkflowThatIsNotThereIsNotFound()
    {
        var world = new World { WorkflowExists = false };

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task The_named_single_replica_dedup_admits_the_fire_that_no_dedup_admitted()
    {
        var request = (WorkflowAdmissionRequest r) => r with { FireDedupKey = "k", IsMultiReplica = true };

        var withoutDedup = await new World { DedupAbsent = true }.AdmitAsync(request);
        var withNamedDedup = await new World { DedupInstead = new SingleReplicaFireDedup() }.AdmitAsync(request);

        withNamedDedup.Outcome.Should().Be(withoutDedup.Outcome);
        withNamedDedup.Reason.Should().Be(withoutDedup.Reason,
            "one process hears one physical event once, so nothing it fires is ever a duplicate");
    }

    [Fact]
    public void The_named_single_replica_dedup_does_not_put_a_fire_key_in_a_log_line()
    {
        const string FireKey = "user:8f2c41/position:47.1,19.4";

        var fingerprint = new SingleReplicaFireDedup().Fingerprint(FireKey);

        fingerprint.Should().NotContain("8f2c41").And.NotContain("47.1");
        fingerprint.Should().NotBeNullOrWhiteSpace()
            .And.NotBe(new SingleReplicaFireDedup().Fingerprint("user:0/position:0,0"),
                "two keys that differ have to be told apart in a log, or the fingerprint says nothing");
    }

    [Fact]
    public async Task A_coordinator_retired_before_the_pin_is_looked_up_again_and_the_run_is_admitted_on_the_fresh_one()
    {
        var world = new World();
        var retired = Parallel();
        retired.TryRetireIfIdle().Should().BeTrue();
        var fresh = Parallel();
        world.Scripted.Enqueue(retired);
        world.Scripted.Enqueue(fresh);

        var result = await world.AdmitAsync();

        result.IsAdmitted.Should().BeTrue();
        fresh.ActiveRunCount.Should().Be(1);
        retired.ActiveRunCount.Should().Be(0);
        result.Decision!.Lease!.Dispose();
    }

    [Fact]
    public async Task A_coordinator_retired_again_on_the_second_lookup_fails_the_admission()
    {
        var world = new World();
        for (var i = 0; i < 2; i++)
        {
            var retired = Parallel();
            retired.TryRetireIfIdle().Should().BeTrue();
            world.Scripted.Enqueue(retired);
        }

        var act = () => world.AdmitAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retired*");
    }

    private static WorkflowRunCoordinator Parallel() =>
        new(Guid.NewGuid(), WorkflowMode.Parallel, 4, 0, WorkflowOverflowBehavior.Drop, new CountingMetrics());

    private sealed class World
    {
        public Guid WorkflowId { get; } = Guid.NewGuid();
        private Guid VersionId { get; } = Guid.NewGuid();
        public bool WorkflowExists { get; init; } = true;
        public bool Published { get; init; } = true;
        public bool OwnerHasScripting { get; set; } = true;
        public bool DefinitionIsPrivileged { get; init; }
        public IWorkflowBlocks? Blocks { get; init; }
        public string DefinitionJson { get; init; } = "{\"nodes\":[],\"edges\":[]}";
        public bool FireAlreadyClaimed { get; init; }
        public bool ExecutionSuspended { get; init; }

        public RecordingStore Store { get; private set; } = null!;
        public RecordingAuthorization Authorization { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public RecordingFireDedup FireDedup { get; } = new();

        public IWorkflowFireDedup? DedupInstead { get; init; }

        public bool DedupAbsent { get; init; }
        public List<WorkflowRunCoordinator> Coordinators { get; } = [];

        public Queue<WorkflowRunCoordinator> Scripted { get; } = new();

        public Task<WorkflowAdmissionResult> AdmitAsync(
            Func<WorkflowAdmissionRequest, WorkflowAdmissionRequest>? tweak = null)
        {
            var workflow = new Workflow(WorkflowId, "wf", null, OwnerId, T0);
            if (Published)
                workflow.SetCurrentVersion(new WorkflowVersion(
                    VersionId, WorkflowId, 1, DefinitionJson, null, null, OwnerId, T0, isPublished: true), T0);

            Store = new RecordingStore(WorkflowExists ? workflow : null);
            Authorization.Answer = OwnerHasScripting;
            FireDedup.Claimable = !FireAlreadyClaimed;

            var blocks = Blocks ?? new FlaggedRegistry(DefinitionIsPrivileged);
            var admission = new WorkflowRunAdmission(
                Store,
                blocks,
                Compilation.CompilationScopes.Service(blocks, Store),
                new RunLifecycleWorld.MemoryJournal(),
                new Gate(ExecutionSuspended),
                Authorization,
                Audit,
                new CountingMetrics(),
                NullLogger.Instance,
                DedupAbsent ? null : DedupInstead ?? FireDedup);

            var request = new WorkflowAdmissionRequest(WorkflowId);
            if (tweak != null)
                request = tweak(request);

            return admission.AdmitAsync(request, Track, CancellationToken.None);
        }

        private WorkflowRunCoordinator Track(Workflow workflow)
        {
            if (Scripted.TryDequeue(out var scripted))
                return scripted;

            var coordinator = new WorkflowRunCoordinator(
                workflow.Id, WorkflowMode.Parallel, 4, 0, WorkflowOverflowBehavior.Drop, new CountingMetrics());
            Coordinators.Add(coordinator);
            return coordinator;
        }
    }

    private sealed class RecordingStore(Workflow? workflow) : IWorkflowStore
    {
        public List<Guid> Loads { get; } = [];

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            Loads.Add(workflowId);
            return Task.FromResult(workflow);
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowVersion?>(null);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid, string)?>(null);
    }

    private sealed class RecordingAuthorization : IWorkflowAuthorization
    {
        public bool Answer { get; set; } = true;
        public List<Guid> AskedAbout { get; } = [];

        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken)
        {
            AskedAbout.Add(principalId);
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        public List<WorkflowAuditEvent> Written { get; } = [];

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Written.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFireDedup : IWorkflowFireDedup
    {
        public bool Claimable { get; set; } = true;
        public List<string> Claims { get; } = [];

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
        {
            Claims.Add(fireKey);
            return Task.FromResult(Claimable);
        }

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class ClaimOnceFireDedup : IWorkflowFireDedup
    {
        public HashSet<string> Claimed { get; } = new(StringComparer.Ordinal);

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult(Claimed.Add(fireKey));

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class StubFactory(string blockTypeId, bool privileged = false) : IWorkflowBlockFactory
    {
        public string BlockTypeId { get; } = blockTypeId;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsPrivileged { get; } = privileged;
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException("Admission never creates a block.");
    }

    private sealed class FlaggedRegistry(bool privileged) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => privileged;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class MutablePrivilegeRegistry : IWorkflowBlocks
    {
        public bool Privileged { get; set; }
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            string.Equals(blockTypeId, "WorkflowInput", StringComparison.OrdinalIgnoreCase)
                ? new StubFactory("WorkflowInput", privileged: Privileged)
                : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => Privileged ? [new StubFactory("Plugin.X", privileged: true)] : [];
    }

    private sealed class Gate(bool suspended) : IWorkflowExecutionGate
    {
        public bool IsExecutionSuspended => suspended;
    }

    private sealed class CountingMetrics : IWorkflowMetrics
    {
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }
}
