using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepParkConditionTests
{
    private const int Short = 30;

    public enum Condition { None, ExecutionTimeout, RetryPolicy, RetryPolicyOfOne, RecordLogEntries, SkippedNode, SkippedLeafNode, NothingSkipped }

    [Theory]
    [InlineData(Condition.None, true)]
    [InlineData(Condition.ExecutionTimeout, false)]
    [InlineData(Condition.RetryPolicy, false)]
    [InlineData(Condition.RetryPolicyOfOne, true)]
    [InlineData(Condition.RecordLogEntries, false)]
    [InlineData(Condition.SkippedNode, false)]
    [InlineData(Condition.SkippedLeafNode, false)]
    [InlineData(Condition.NothingSkipped, true)]
    public async Task A_run_parks_only_when_nothing_it_holds_in_the_process_would_be_lost(Condition condition, bool parks)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.FlakyFails = condition is Condition.SkippedNode or Condition.SkippedLeafNode;
        var workflowId = condition is Condition.SkippedNode or Condition.SkippedLeafNode or Condition.NothingSkipped
            ? world.AddRaw(SkipGraph(withSide: condition != Condition.SkippedLeafNode))
            : world.AddWorkflow(Short);
        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        if (condition == Condition.ExecutionTimeout)
            workflow.Update("wf", null, DateTimeOffset.UtcNow, executionTimeoutSeconds: 60);
        if (condition is Condition.RetryPolicy or Condition.RetryPolicyOfOne)
            workflow.Update("wf", null, DateTimeOffset.UtcNow,
                retryPolicyJson: JsonSerializer.Serialize(new { maxAttempts = condition == Condition.RetryPolicy ? 2 : 1 }));
        using var engine = world.Engine();

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { RecordLogEntries = condition == Condition.RecordLogEntries }))
            await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.Rows.Should().HaveCount(parks ? 1 : 0);
        world.Blocks.AfterInputs.Should().HaveCount(parks ? 0 : 1, "a sleep that did not park finished in the process");
        if (condition is Condition.SkippedNode or Condition.SkippedLeafNode)
        {
            world.Blocks.FlakyCalls.Should().Be(1, "a node skipped by its own policy with nothing downstream is not run again either");
            world.Blocks.SideCalls.Should().Be(0, "the node the Skip policy skipped stays skipped");
        }
    }

    private static string SkipGraph(bool withSide)
    {
        var nodes = new List<object>
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "flaky", blockTypeId = DurableSleepWorld.FlakyType, errorPolicy = new { policy = nameof(NodeErrorPolicy.Skip) }, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType, configuration = new { DurationMs = Short }, position = new { x = 0, y = 0 } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        };
        var edges = new List<object>
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = "flaky", mappings = new { } },
            new { id = "e3", sourceNodeId = "start", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
            new { id = "e4", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        };
        if (withSide)
        {
            nodes.Insert(2, new { id = "side", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } });
            edges.Insert(1, new { id = "e2", sourceNodeId = "flaky", targetNodeId = "side", mappings = new { } });
        }

        return JsonSerializer.Serialize(new { nodes, edges });
    }
}
