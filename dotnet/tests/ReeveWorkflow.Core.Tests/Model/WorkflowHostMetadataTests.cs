using FluentAssertions;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowHostMetadataTests
{
    private static Dictionary<string, string> Map(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    public static TheoryData<IReadOnlyDictionary<string, string>, string> Refused()
    {
        var seventeen = Enumerable.Range(0, 17).ToDictionary(i => $"k{i}", _ => "v");
        return new TheoryData<IReadOnlyDictionary<string, string>, string>
        {
            { seventeen, "17 entries" },
            { Map(("", "v")), "key ''" },
            { Map(("1abc", "v")), "key '1abc'" },
            { Map(("_abc", "v")), "key '_abc'" },
            { Map(("a b", "v")), "key 'a b'" },
            { Map(("é", "v")), "key 'é'" },
            { Map((new string('a', 65), "v")), "refused" },
            { Map(("note", new string('x', 257))), "entry 'note'" },
            { Map(("note", "line\nbreak")), "entry 'note'" },
            { Map(("note", "tab\there")), "entry 'note'" },
        };
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_map_over_a_limit_is_refused_naming_the_entry(IReadOnlyDictionary<string, string> metadata, string names)
    {
        WorkflowHostMetadata.Refusal(metadata).Should().Contain(names);
        FluentActions.Invoking(() => new Workflow(Guid.NewGuid(), "Flow", null, Guid.NewGuid(), DateTimeOffset.UtcNow, metadata))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new Workflow(Guid.NewGuid(), "Flow", null, Guid.NewGuid(), DateTimeOffset.UtcNow)
                .Update("Flow", null, DateTimeOffset.UtcNow, metadata))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_map_at_every_limit_is_stored_as_a_copy_that_later_edits_to_the_original_do_not_reach()
    {
        var atLimit = Enumerable.Range(0, 16).ToDictionary(i => $"k{i}", _ => new string('v', 256));
        atLimit["a" + new string('-', 63)] = "ok";
        atLimit.Remove("k15");
        var workflow = new Workflow(Guid.NewGuid(), "Flow", null, Guid.NewGuid(), DateTimeOffset.UtcNow, atLimit);

        atLimit["k0"] = "changed";

        workflow.HostMetadata.Should().NotBeNull();
        workflow.HostMetadata!["k0"].Should().Be(new string('v', 256));
        workflow.HostMetadata.Should().HaveCount(16);
    }

    [Fact]
    public void An_empty_map_is_stored_as_null_and_an_update_replaces_the_whole_map()
    {
        var workflow = new Workflow(Guid.NewGuid(), "Flow", null, Guid.NewGuid(), DateTimeOffset.UtcNow, new Dictionary<string, string>());
        workflow.HostMetadata.Should().BeNull();

        workflow.Update("Flow", null, DateTimeOffset.UtcNow, Map(("a", "1"), ("b", "2")));
        workflow.Update("Flow", null, DateTimeOffset.UtcNow, Map(("c", "3")));
        workflow.HostMetadata.Should().BeEquivalentTo(Map(("c", "3")));

        workflow.Update("Flow", null, DateTimeOffset.UtcNow, Map());
        workflow.HostMetadata.Should().BeNull();

        workflow.Update("Flow", null, DateTimeOffset.UtcNow, Map(("c", "3")));
        workflow.Update("Flow", null, DateTimeOffset.UtcNow, null);
        workflow.HostMetadata.Should().BeNull();
    }
}
