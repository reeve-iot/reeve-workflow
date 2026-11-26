using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowTransitionRecorderTests
{
    private sealed class RecordingRunStateStore(Exception? failWith = null, bool answer = true)
        : IWorkflowRunStateStore
    {
        public List<WorkflowRunStateTransition> Transitions { get; } = [];

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowRunStateRow?>(null);

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            if (failWith is not null) return Task.FromException<bool>(failWith);
            Transitions.Add(request);
            return Task.FromResult(answer);
        }

        public Task<bool> CompleteAsync(
            Guid executionId,
            WorkflowRunStateStatus terminalStatus,
            string? failureReason,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingAuditSink(Exception? failWith = null) : IWorkflowAuditSink
    {
        public List<WorkflowAuditEvent> Events { get; } = [];

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (failWith is not null) return Task.FromException(failWith);
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static WorkflowTransitionRecorder Recorder(
        IWorkflowRunStateStore? store = null,
        string? replicaId = null,
        bool auditTransitions = false,
        IWorkflowAuditSink? auditSink = null,
        ILogger? logger = null) =>
        new(store, logger ?? NullLogger.Instance,
            new WorkflowTransitionOptions { ReplicaId = replicaId, AuditTransitions = auditTransitions },
            auditSink);

    private static WorkflowPreBlockState State(Guid executionId) => new(
        StateExecutionId: executionId,
        WorkflowId: Guid.NewGuid(),
        NodeId: "node-7",
        BlockTypeId: "Log",
        Variables: new Dictionary<string, object?> { ["greeting"] = "hello" });

    [Fact]
    public void Run_state_is_tracked_exactly_when_a_store_was_supplied()
    {
        Recorder().TracksRunState.Should().BeFalse();
        Recorder(new RecordingRunStateStore()).TracksRunState.Should().BeTrue();
    }

    [Fact]
    public async Task A_pre_block_write_records_where_the_run_is_without_changing_its_status()
    {
        var store = new RecordingRunStateStore();
        var executionId = Guid.NewGuid();

        var stillOurs = await Recorder(store, replicaId: "replica-3")
            .TryWritePreBlockStateAsync(State(executionId), default);

        stillOurs.Should().BeTrue("the store took the row");

        var transition = store.Transitions.Should().ContainSingle().Subject;
        transition.ExecutionId.Should().Be(executionId);
        transition.FromStatus.Should().Be(WorkflowRunStateStatus.Running);
        transition.ToStatus.Should().Be(WorkflowRunStateStatus.Running);
        transition.CurrentBlockPath.Should().Be("node-7");
        transition.OwnedByReplicaId.Should().Be("replica-3");
        transition.VariablesJson.Should().Be(
            WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["greeting"] = "hello" }));
    }

    [Fact]
    public async Task The_row_is_only_taken_while_it_is_still_ours()
    {
        var store = new RecordingRunStateStore();

        await Recorder(store, replicaId: "replica-3").TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        store.Transitions.Should().ContainSingle().Which.ExpectedOwnedByReplicaId.Should().Be("replica-3");
    }

    [Fact]
    public async Task A_failed_pre_block_write_does_not_cost_the_node_that_was_about_to_run()
    {
        var recorder = Recorder(new RecordingRunStateStore(failWith: new InvalidOperationException("database is away")));

        var act = () => recorder.TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        (await act.Should().NotThrowAsync()).Which.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_is_written_when_no_store_was_supplied()
    {
        var act = () => Recorder().TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        (await act.Should().NotThrowAsync()).Which.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_that_keeps_a_trail_gets_the_transition_as_an_audit_event()
    {
        var audit = new RecordingAuditSink();
        var executionId = Guid.NewGuid();

        await Recorder(new RecordingRunStateStore(), auditTransitions: true, auditSink: audit)
            .TryWritePreBlockStateAsync(State(executionId), default);

        var written = audit.Events.Should().ContainSingle().Subject;
        written.Kind.Should().Be(WorkflowAuditEventKinds.ExecutionTransitioned);
        written.SubjectId.Should().Be(executionId);
    }

    [Fact]
    public async Task The_audit_event_never_carries_the_variables()
    {
        var audit = new RecordingAuditSink();

        await Recorder(new RecordingRunStateStore(), auditTransitions: true, auditSink: audit)
            .TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        System.Text.Json.JsonSerializer.Serialize(audit.Events.Single().Data)
            .Should().NotContain("hello").And.NotContain("greeting");
    }

    [Fact]
    public async Task A_host_that_keeps_no_trail_pays_for_none()
    {
        var audit = new RecordingAuditSink();

        await Recorder(new RecordingRunStateStore(), auditTransitions: false, auditSink: audit)
            .TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_that_could_not_be_written_is_not_audited_as_a_transition_that_happened()
    {
        var audit = new RecordingAuditSink();
        var recorder = Recorder(
            new RecordingRunStateStore(failWith: new InvalidOperationException("database is away")),
            auditTransitions: true,
            auditSink: audit);

        await recorder.TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        audit.Events.Should().BeEmpty("the trail would claim a position the row does not record");
    }

    [Fact]
    public async Task A_row_lost_to_a_peer_is_not_audited_as_a_transition_that_happened()
    {
        var audit = new RecordingAuditSink();
        var recorder = Recorder(
            new RecordingRunStateStore(answer: false), auditTransitions: true, auditSink: audit);

        await recorder.TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_a_row_a_peer_has_taken_is_answered_as_lost()
    {
        var lost = await Recorder(new RecordingRunStateStore(answer: false))
            .TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);
        var unreachable = await Recorder(new RecordingRunStateStore(failWith: new InvalidOperationException("database is away")))
            .TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);
        var untracked = await Recorder().TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        lost.Should().BeFalse("the store said the row is a peer's now");
        unreachable.Should().BeTrue("a write that did not happen says nothing about who owns the row");
        untracked.Should().BeTrue();
    }

    [Fact]
    public async Task A_row_lost_to_a_peer_says_so()
    {
        var logger = new CountingLogger();
        var executionId = Guid.NewGuid();

        await Recorder(new RecordingRunStateStore(answer: false), replicaId: "replica-3", logger: logger)
            .TryWritePreBlockStateAsync(State(executionId), default);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("node-7").And.Contain(executionId.ToString());
    }

    [Fact]
    public async Task A_failed_audit_write_does_not_cost_the_node_either()
    {
        var recorder = Recorder(
            new RecordingRunStateStore(),
            auditTransitions: true,
            auditSink: new RecordingAuditSink(failWith: new InvalidOperationException("the sink is away")));

        var act = () => recorder.TryWritePreBlockStateAsync(State(Guid.NewGuid()), default);

        await act.Should().NotThrowAsync();
    }

    private sealed class LostSwapStore(Func<WorkflowRunStateRow?> readBack) : IWorkflowRunStateStore
    {
        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult(readBack());

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> CompleteAsync(Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static WorkflowRunStateRow RowNow(WorkflowRunStateStatus status, string? owner) => new()
    {
        ExecutionId = Guid.NewGuid(),
        WorkflowId = Guid.NewGuid(),
        Status = status,
        OwnedByReplicaId = owner
    };

    [Theory]
    [InlineData(WorkflowRunStateStatus.Running, "replica-9", WorkflowRunOwnershipLoss.ReclaimedByPeer)]
    [InlineData(WorkflowRunStateStatus.Running, null, WorkflowRunOwnershipLoss.Released)]
    [InlineData(WorkflowRunStateStatus.Cancelled, "replica-3", WorkflowRunOwnershipLoss.StatusMoved)]
    [InlineData(WorkflowRunStateStatus.Completed, null, WorkflowRunOwnershipLoss.StatusMoved)]
    public async Task A_lost_row_is_explained_by_what_it_says_now(
        WorkflowRunStateStatus status, string? owner, WorkflowRunOwnershipLoss expected)
    {
        var recorder = Recorder(new LostSwapStore(() => RowNow(status, owner)), replicaId: "replica-3");

        var loss = await recorder.ExplainLossAsync(Guid.NewGuid(), WorkflowRunStateStatus.Running, default);

        loss.Should().Be(expected);
    }

    [Fact]
    public async Task A_row_that_is_gone_is_explained_as_gone()
    {
        var recorder = Recorder(new LostSwapStore(() => null), replicaId: "replica-3");

        var loss = await recorder.ExplainLossAsync(Guid.NewGuid(), WorkflowRunStateStatus.Running, default);

        loss.Should().Be(WorkflowRunOwnershipLoss.RowMissing);
    }

    [Fact]
    public async Task A_row_that_cannot_be_read_back_is_not_given_a_reason_it_does_not_have()
    {
        var recorder = Recorder(
            new LostSwapStore(() => throw new InvalidOperationException("database is away")), replicaId: "replica-3");

        var loss = await recorder.ExplainLossAsync(Guid.NewGuid(), WorkflowRunStateStatus.Running, default);

        loss.Should().Be(WorkflowRunOwnershipLoss.Unknown);
    }

    [Fact]
    public async Task A_row_that_reads_back_as_still_ours_is_not_explained_as_taken()
    {
        var recorder = Recorder(
            new LostSwapStore(() => RowNow(WorkflowRunStateStatus.Running, "replica-3")), replicaId: "replica-3");

        var loss = await recorder.ExplainLossAsync(Guid.NewGuid(), WorkflowRunStateStatus.Running, default);

        loss.Should().Be(WorkflowRunOwnershipLoss.Unknown);
    }

    [Theory]
    [InlineData(WorkflowRunOwnershipLoss.ReclaimedByPeer, "another replica")]
    [InlineData(WorkflowRunOwnershipLoss.Released, "released")]
    [InlineData(WorkflowRunOwnershipLoss.StatusMoved, "status")]
    [InlineData(WorkflowRunOwnershipLoss.RowMissing, "no longer exists")]
    [InlineData(WorkflowRunOwnershipLoss.Unknown, "could not be read back")]
    public void The_exception_says_the_reason_it_carries_and_no_other(WorkflowRunOwnershipLoss loss, string phrase)
    {
        var thrown = new WorkflowRunOwnershipLostException(Guid.NewGuid(), "node-7", loss);

        thrown.Loss.Should().Be(loss);
        thrown.Message.Should().Contain(phrase).And.Contain("node-7");
        if (loss != WorkflowRunOwnershipLoss.ReclaimedByPeer)
            thrown.Message.Should().NotContain("another replica", "only a peer's takeover is a peer's takeover");
    }
}
