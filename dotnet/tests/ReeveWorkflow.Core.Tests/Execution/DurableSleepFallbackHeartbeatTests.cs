using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepFallbackHeartbeatTests
{
    private static string Definition(int sleepMs) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType,
                  configuration = new Dictionary<string, object?> { ["DurationMs"] = sleepMs }, position = new { x = 0, y = 0 } },
            new { id = "hold", blockTypeId = DurableSleepWorld.HoldType, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
            new { id = "e2", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = "hold", mappings = new { } },
            new { id = "e3", sourceNodeId = "hold", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    [Fact]
    public async Task A_sleep_that_falls_back_to_this_process_keeps_renewing_its_claim()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RecoverySeam = new MemoryRunRecovery(world.RunState);
        world.RunHeartbeatInterval = TimeSpan.FromMilliseconds(20);
        var workflowId = world.AddRaw(Definition(20));
        using var a = world.Engine("replica-1");

        world.RunState.FailParks = 3;
        world.RunState.FailParksAs = SuspendFailure.BeforeWrite;

        var run = a.ExecuteAsync(workflowId, null, null);
        await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var beatsBefore = world.RecoverySeam.Renewals;

        await DurableSleepWorld.EventuallyAsync(
            () => world.RecoverySeam.Renewals > beatsBefore,
            "the run that stayed here to renew its claim, so the sweep does not replay it");

        world.Blocks.Release.TrySetResult();
        await run;
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
    }
}
