using FluentAssertions;

namespace ReeveWorkflow.Editor.Tests;

public sealed class EditorDoorPairTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [MemberData(nameof(EditorDoorPairs.Pairs), MemberType = typeof(EditorDoorPairs))]
    public async Task Two_doors_on_one_scope_answer_as_they_do_on_two(string first, string second)
    {
        using var db = new SqliteEditorDatabase();

        var twoScopes = await EditorDoorPairs.RunAsync(db.Services, Owner, first, second, oneScope: false);
        var oneScope = await EditorDoorPairs.RunAsync(db.Services, Owner, first, second, oneScope: true);

        oneScope.Should().Be(twoScopes);
    }
}
