using FluentAssertions;

namespace ReeveWorkflow.Editor.Tests;

public sealed class EditorDoorSequenceTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Sequences_on_one_scope_answer_as_on_a_scope_per_step()
    {
        using var db = new SqliteEditorDatabase();

        var differing = await EditorDoorSequences.DifferingAsync(db.Services, Owner,
            EditorDoorSequences.Exhaustive(3).Concat(EditorDoorSequences.Random(400, 5, seed: 17)));

        differing.Should().BeEmpty();
    }
}
