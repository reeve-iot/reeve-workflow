using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ParkedResumeGateTests
{
    [Fact]
    public async Task A_parked_run_whose_version_stopped_compiling_is_abandoned_as_pinned_version_uncompilable()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(600_000);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(10));
        var row = world.Suspensions.SingleRow;

        world.Blocks.Extra = id => id == DurableSleepWorld.SleepType
            ? new LambdaFactory(id, _ => new Dictionary<string, object?>())
            : null;
        await FluentActions.Awaiting(() => engine.ExecuteAsync(workflowId, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>("a start of the same version is judged now");

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc),
            world.Suspensions.Now, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        var run = world.Journal.Runs.Values.Should().ContainSingle(r => r.FailureReason == null, "the refused start wrote a row of its own").Which;
        run.Status.Should().Be(WorkflowExecutionStatus.Failed);
        run.ErrorMessage.Should().StartWith($"pinned_version_uncompilable: {run.WorkflowVersionId}");
        world.Blocks.AfterInputs.Should().BeEmpty("no node ran on a version that has no program");
    }
}
