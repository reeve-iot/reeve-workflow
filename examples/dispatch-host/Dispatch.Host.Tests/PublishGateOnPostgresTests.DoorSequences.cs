using FluentAssertions;
using ReeveWorkflow.Editor.Tests;

namespace Dispatch.Host.Tests;

public sealed partial class PublishGateOnPostgresTests
{
    [Fact]
    public async Task Editor_step_sequences_on_one_scope_answer_as_on_a_scope_per_step()
    {
        await using var desk = await DeskAsync("door_sequences");

        var differing = await EditorDoorSequences.DifferingAsync(desk.Services, Owner, EditorDoorSequences.Random(250, 4, seed: 23));

        differing.Should().BeEmpty();
    }
}
