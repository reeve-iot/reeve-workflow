using FluentAssertions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowDebugSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 25, 10, 0, 0, TimeSpan.Zero);

    private static WorkflowDebugSession CreateSession() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);

    [Fact]
    public void Constructor_creates_session_with_Idle_status()
    {
        var session = CreateSession();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Idle);
    }

    [Fact]
    public void Constructor_requires_non_empty_id()
    {
        var act = () => new WorkflowDebugSession(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), T0);
        act.Should().Throw<ArgumentException>().WithMessage("*Session id*");
    }

    [Fact]
    public void Constructor_requires_non_empty_workflowId()
    {
        var act = () => new WorkflowDebugSession(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), T0);
        act.Should().Throw<ArgumentException>().WithMessage("*Workflow id*");
    }

    [Fact]
    public void Constructor_requires_non_empty_ownerUserId()
    {
        var act = () => new WorkflowDebugSession(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, T0);
        act.Should().Throw<ArgumentException>().WithMessage("*Owner user id*");
    }

    [Fact]
    public void Constructor_sets_CreatedAtUtc_and_LastActivityUtc()
    {
        var session = CreateSession();
        session.CreatedAtUtc.Should().Be(T0);
        session.LastActivityUtc.Should().Be(T0);
    }

    [Fact]
    public void Constructor_creates_session_with_expected_object_storage_key()
    {
        var id = Guid.NewGuid();
        var session = new WorkflowDebugSession(id, Guid.NewGuid(), Guid.NewGuid(), T0);
        session.ObjectStorageKey.Should().Be(id.ToString());
    }

    [Fact]
    public void Idle_to_Bound_is_legal()
    {
        var session = CreateSession();
        var act = () => session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Bound);
    }

    [Fact]
    public void Bound_to_Running_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Running);
    }

    [Fact]
    public void Running_to_Paused_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(3));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Paused);
    }

    [Fact]
    public void Paused_to_Running_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(3));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(4));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Running);
    }

    [Fact]
    public void Running_to_Paused_to_Running_cycle_is_repeatable()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));

        for (var i = 0; i < 5; i++)
        {
            session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(3 + i * 2));
            session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(4 + i * 2));
        }

        session.Status.Should().Be(WorkflowDebugSessionStatus.Running);
    }

    [Fact]
    public void Running_to_Completed_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Completed, T0.AddSeconds(3));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Completed);
    }

    [Fact]
    public void Running_to_Cancelled_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Cancelled, T0.AddSeconds(3));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Cancelled);
    }

    [Fact]
    public void Paused_to_Cancelled_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(3));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Cancelled, T0.AddSeconds(4));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Cancelled);
    }

    [Fact]
    public void Bound_to_Cancelled_is_legal()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Cancelled, T0.AddSeconds(2));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Cancelled);
    }

    [Fact]
    public void Idle_to_Cancelled_is_legal()
    {
        var session = CreateSession();
        var act = () => session.Transition(WorkflowDebugSessionStatus.Cancelled, T0.AddSeconds(1));
        act.Should().NotThrow();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Cancelled);
    }

    [Fact]
    public void Full_lifecycle_Idle_Bound_Running_Paused_Running_Completed_succeeds()
    {
        var session = CreateSession();
        session.Status.Should().Be(WorkflowDebugSessionStatus.Idle);

        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Status.Should().Be(WorkflowDebugSessionStatus.Bound);

        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        session.Status.Should().Be(WorkflowDebugSessionStatus.Running);

        session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(3));
        session.Status.Should().Be(WorkflowDebugSessionStatus.Paused);

        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(4));
        session.Status.Should().Be(WorkflowDebugSessionStatus.Running);

        session.Transition(WorkflowDebugSessionStatus.Completed, T0.AddSeconds(5));
        session.Status.Should().Be(WorkflowDebugSessionStatus.Completed);
    }

    [Fact]
    public void Idle_to_Paused_throws_InvalidOperationException()
    {
        var session = CreateSession();
        var act = () => session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(1));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Idle*Paused*");
    }

    [Fact]
    public void Idle_to_Running_throws_InvalidOperationException()
    {
        var session = CreateSession();
        var act = () => session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Idle_to_Completed_throws_InvalidOperationException()
    {
        var session = CreateSession();
        var act = () => session.Transition(WorkflowDebugSessionStatus.Completed, T0.AddSeconds(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Bound_to_Paused_throws_InvalidOperationException()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Paused, T0.AddSeconds(2));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Bound_to_Completed_throws_InvalidOperationException()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Completed, T0.AddSeconds(2));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Running_to_Bound_throws_InvalidOperationException()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        var act = () => session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(3));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Completed_to_any_throws_InvalidOperationException()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));
        session.Transition(WorkflowDebugSessionStatus.Completed, T0.AddSeconds(3));

        var targets = new[]
        {
            WorkflowDebugSessionStatus.Idle,
            WorkflowDebugSessionStatus.Bound,
            WorkflowDebugSessionStatus.Running,
            WorkflowDebugSessionStatus.Paused,
            WorkflowDebugSessionStatus.Cancelled,
            WorkflowDebugSessionStatus.Failed,
        };

        foreach (var target in targets)
        {
            var act = () => session.Transition(target, T0.AddSeconds(4));
            act.Should().Throw<InvalidOperationException>(
                $"Completed -> {target} should be illegal");
        }
    }

    [Fact]
    public void Cancelled_to_any_throws_InvalidOperationException()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Cancelled, T0.AddSeconds(1));

        var targets = new[]
        {
            WorkflowDebugSessionStatus.Idle,
            WorkflowDebugSessionStatus.Bound,
            WorkflowDebugSessionStatus.Running,
            WorkflowDebugSessionStatus.Paused,
            WorkflowDebugSessionStatus.Completed,
            WorkflowDebugSessionStatus.Failed,
        };

        foreach (var target in targets)
        {
            var act = () => session.Transition(target, T0.AddSeconds(2));
            act.Should().Throw<InvalidOperationException>(
                $"Cancelled -> {target} should be illegal");
        }
    }

    [Fact]
    public void A_running_session_can_fail_and_nothing_follows_a_failure()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        session.Transition(WorkflowDebugSessionStatus.Running, T0.AddSeconds(2));

        session.Transition(WorkflowDebugSessionStatus.Failed, T0.AddSeconds(3));

        session.Status.Should().Be(WorkflowDebugSessionStatus.Failed);
        foreach (var target in Enum.GetValues<WorkflowDebugSessionStatus>())
        {
            var act = () => session.Transition(target, T0.AddSeconds(4));
            act.Should().Throw<InvalidOperationException>($"Failed -> {target} should be illegal");
        }
    }

    [Theory]
    [InlineData(WorkflowDebugSessionStatus.Idle, false)]
    [InlineData(WorkflowDebugSessionStatus.Bound, true)]
    [InlineData(WorkflowDebugSessionStatus.Running, true)]
    [InlineData(WorkflowDebugSessionStatus.Paused, true)]
    public void A_session_bound_to_a_run_can_fail_and_an_idle_one_cannot(WorkflowDebugSessionStatus from, bool fails)
    {
        var session = CreateSession();
        foreach (var step in new[] { WorkflowDebugSessionStatus.Bound, WorkflowDebugSessionStatus.Running, WorkflowDebugSessionStatus.Paused })
        {
            if (session.Status == from)
                break;
            session.Transition(step, T0.AddSeconds(1));
        }

        session.Status.Should().Be(from, "setup");
        var act = () => session.Transition(WorkflowDebugSessionStatus.Failed, T0.AddSeconds(2));
        if (fails)
            act.Should().NotThrow();
        else
            act.Should().Throw<InvalidOperationException>().WithMessage("*Idle*Failed*");
    }

    [Fact]
    public void Transition_updates_LastActivityUtc()
    {
        var session = CreateSession();
        var t1 = T0.AddSeconds(5);
        session.Transition(WorkflowDebugSessionStatus.Bound, t1);
        session.LastActivityUtc.Should().Be(t1);

        var t2 = t1.AddSeconds(10);
        session.Transition(WorkflowDebugSessionStatus.Running, t2);
        session.LastActivityUtc.Should().Be(t2);
    }

    [Fact]
    public void BindToExecution_sets_WorkflowExecutionLogId_and_transitions_to_Bound()
    {
        var session = CreateSession();
        var logId = Guid.NewGuid();
        session.BindToExecution(logId, T0.AddSeconds(1));
        session.WorkflowExecutionLogId.Should().Be(logId);
        session.Status.Should().Be(WorkflowDebugSessionStatus.Bound);
    }

    [Fact]
    public void BindToExecution_from_non_Idle_state_throws()
    {
        var session = CreateSession();
        session.Transition(WorkflowDebugSessionStatus.Bound, T0.AddSeconds(1));
        var act = () => session.BindToExecution(Guid.NewGuid(), T0.AddSeconds(2));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WorkflowDebugSnapshot_constructor_requires_non_empty_id()
    {
        var act = () => new WorkflowDebugSnapshot(Guid.Empty, Guid.NewGuid(), "n1", T0);
        act.Should().Throw<ArgumentException>().WithMessage("*Snapshot id*");
    }

    [Fact]
    public void WorkflowDebugSnapshot_constructor_requires_non_empty_debugSessionId()
    {
        var act = () => new WorkflowDebugSnapshot(Guid.NewGuid(), Guid.Empty, "n1", T0);
        act.Should().Throw<ArgumentException>().WithMessage("*Debug session id*");
    }

    [Fact]
    public void WorkflowDebugSnapshot_constructor_requires_non_null_nodeId()
    {
        var act = () => new WorkflowDebugSnapshot(Guid.NewGuid(), Guid.NewGuid(), null!, T0);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WorkflowDebugSnapshot_constructor_initializes_thin_fields()
    {
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var snap = new WorkflowDebugSnapshot(id, sessionId, "n5", T0);
        snap.Id.Should().Be(id);
        snap.DebugSessionId.Should().Be(sessionId);
        snap.NodeId.Should().Be("n5");
        snap.CapturedAtUtc.Should().Be(T0);
    }

    [Fact]
    public void WorkflowDebugSnapshot_ObjectStorageKey_has_expected_format()
    {
        var sessionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var snap = new WorkflowDebugSnapshot(snapshotId, sessionId, "n5", T0);
        snap.ObjectStorageKey.Should().Be($"{sessionId}/snapshots/{snapshotId}");
    }
}
