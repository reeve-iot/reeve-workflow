using System.Reflection;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class SuspensionStatePayloadRelinkTests
{
    private static WorkflowSuspensionStatePayload Filled() => new()
    {
        VariablesJson = """{"who":"alice"}""",
        PreviousOutputsJson = """{"start":{"who":"alice"}}""",
        TriggerEnvelopeJson = """{"triggerId":"11111111-1111-1111-1111-111111111111"}""",
        ExecutionStateId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PinnedVersionsJson = """{"33333333-3333-3333-3333-333333333333":"44444444-4444-4444-4444-444444444444"}""",
        InputNodeId = "start",
        CallDepth = 3,
        JournalExecutionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        CheckpointFormat = WorkflowRunCheckpoint.Format,
        NodeInFlight = "label",
        WriteMode = nameof(RunStateWriteModeKind.BeforeNonIdempotentOnly),
        ContainerInFlightRepeatsWhole = true,
        NotReenterableBecause = WorkflowRunCheckpoint.SkippedANode,
        ContinuationJson = """[{"subCanvas":"Body","nodeInFlight":"inside"}]""",
        ParentExecutionId = Guid.Parse("77777777-7777-7777-7777-777777777777")
    };

    [Fact]
    public void The_row_id_a_park_puts_in_is_the_only_thing_that_differs()
    {
        var rowId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var before = Filled();

        var after = before.WithExecutionStateId(rowId);

        after.ExecutionStateId.Should().Be(rowId);
        foreach (var property in typeof(WorkflowSuspensionStatePayload).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(WorkflowSuspensionStatePayload.ExecutionStateId))
                continue;
            property.GetValue(after).Should().Be(property.GetValue(before),
                $"a park may relink the row, and '{property.Name}' is not the row");
        }
    }

    [Fact]
    public void A_property_the_copy_cannot_reach_is_not_silently_dropped()
    {
        var filled = Filled();
        foreach (var property in typeof(WorkflowSuspensionStatePayload).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(filled);
            var fallback = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            value.Should().NotBe(fallback,
                $"'{property.Name}' is left at its default by this test's payload, so the copy test cannot see it dropped");
        }
    }
}
