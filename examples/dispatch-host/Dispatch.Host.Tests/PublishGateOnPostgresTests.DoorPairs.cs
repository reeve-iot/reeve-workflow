using FluentAssertions;
using ReeveWorkflow.Editor.Tests;

namespace Dispatch.Host.Tests;

public sealed partial class PublishGateOnPostgresTests
{
    [Theory]
    [MemberData(nameof(EditorDoorPairs.Pairs), MemberType = typeof(EditorDoorPairs))]
    public async Task Two_editor_doors_on_one_scope_answer_as_they_do_on_two(string first, string second)
    {
        await using var desk = await DeskAsync($"pairs_{first}_{second}");

        var twoScopes = await EditorDoorPairs.RunAsync(desk.Services, Owner, first, second, oneScope: false);
        var oneScope = await EditorDoorPairs.RunAsync(desk.Services, Owner, first, second, oneScope: true);

        oneScope.Should().Be(twoScopes);
    }
}
