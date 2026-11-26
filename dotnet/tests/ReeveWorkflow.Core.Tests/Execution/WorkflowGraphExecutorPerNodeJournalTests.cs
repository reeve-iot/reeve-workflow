using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorPerNodeJournalTests
{
    [Fact]
    public async Task Per_node_entries_are_recorded_only_for_debug_runs()
    {
        var normal = await ExecuteAsync(debug: false);
        normal.Entries.Should().BeEmpty("the walk must not offer per-node entries to the journal in a normal run");
        normal.Run.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        var debug = await ExecuteAsync(debug: true);
        debug.Run.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        var perNode = debug.Entries.Where(entry => entry.Level == LogEntryLevel.Debug).ToArray();
        perNode.Should().HaveCount(4);
        perNode.GroupBy(entry => entry.NodeId).Select(group => group.Count()).Should().OnlyContain(count => count == 2);
    }

    private static async Task<(WorkflowExecutionLog Run, WorkflowExecutionLogEntry[] Entries)> ExecuteAsync(bool debug)
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true);
        world.Release.SetResult();
        using var engine = world.Engine();

        if (debug)
        {
            using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { RecordLogEntries = true }))
                await engine.ExecuteAsync(world.WorkflowId, null, null);
        }
        else
        {
            await engine.ExecuteAsync(world.WorkflowId, null, null);
        }

        var run = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        var entries = world.Journal.Entries.TryGetValue(run.Id, out var recorded) ? recorded.ToArray() : [];
        return (run, entries);
    }
}
