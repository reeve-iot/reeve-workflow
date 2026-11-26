using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepWithdrawTests
{
    private const string SleepNodeId = "sleep";

    private static readonly Guid ExecutionId = Guid.NewGuid();

    [Fact]
    public async Task A_park_that_was_not_recorded_past_the_cap_leaves_an_earlier_step_of_the_same_node()
    {
        var earlier = Step(SleepNodeId, 0);
        var steps = new List<WorkflowExecutionStep> { Step("start", 1), earlier };

        await WithdrawAsync(Sleep(recorded: null), steps);

        steps.Should().HaveCount(2).And.Contain(earlier,
            "the step is the loop's previous pass through the sleep; the park that failed recorded none of its own");
    }

    [Fact]
    public async Task A_park_that_was_recorded_takes_back_its_own_step_and_no_other()
    {
        var earlier = Step(SleepNodeId, 0);
        var park = Step(SleepNodeId, 1);
        var steps = new List<WorkflowExecutionStep> { earlier, park };

        await WithdrawAsync(Sleep(recorded: park), steps);

        steps.Should().ContainSingle().Which.Should().BeSameAs(earlier);
    }

    private static Task WithdrawAsync(SuspendedWorkflowException sleep, List<WorkflowExecutionStep> steps) =>
        WorkflowDurableSleep.WithdrawFailedParkAsync(
            Substitute.For<IWorkflowSuspensionStore>(), NullLogger.Instance, new TimeoutException("the park timed out"),
            sleep, ExecutionId, steps);

    private static SuspendedWorkflowException Sleep(WorkflowExecutionStep? recorded) =>
        new(new SuspendResult(WorkflowSuspensionKind.Sleep, "{}", DateTimeOffset.UtcNow.AddMinutes(1)),
            SleepNodeId, ExecutionId, Guid.NewGuid(), "{}")
        {
            RecordedStep = recorded
        };

    private static WorkflowExecutionStep Step(string nodeId, int sortOrder) =>
        new(Guid.NewGuid(), ExecutionId, nodeId, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0,
            WorkflowExecutionStepStatus.Succeeded, sortOrder, null, null, null, null);
}
