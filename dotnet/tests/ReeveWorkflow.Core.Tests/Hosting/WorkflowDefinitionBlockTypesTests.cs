using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Hosting;

public class WorkflowDefinitionBlockTypesTests
{
    public static TheoryData<string, string> DocumentsWithASecondCasing => new()
    {
        { "two node lists", """{"nodes":[{"id":"a","blockTypeId":"Log"}],"Nodes":[{"id":"b","blockTypeId":"Script"}],"edges":[]}""" },
        { "two ids on one node", """{"nodes":[{"id":"a","blockTypeId":"Log","BlockTypeId":"Script"}],"edges":[]}""" },
        {
            "two sub-canvas maps",
            """{"nodes":[{"id":"a","blockTypeId":"Loop","subCanvases":{"body":{"nodes":[{"id":"b","blockTypeId":"Log"}],"edges":[]}},"SubCanvases":{"body":{"nodes":[{"id":"c","blockTypeId":"Script"}]}}}],"edges":[]}"""
        },
        {
            "two node lists inside a sub-canvas",
            """{"nodes":[{"id":"a","blockTypeId":"Loop","subCanvases":{"body":{"nodes":[{"id":"b","blockTypeId":"Log"}],"NODES":[{"id":"c","blockTypeId":"Script"}],"edges":[]}}}],"edges":[]}"""
        },
    };

    [Theory]
    [MemberData(nameof(DocumentsWithASecondCasing))]
    public void The_collector_names_every_block_the_engine_reads(string shape, string json)
    {
        var engineReads = BlockTypeIds(JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowGraphHelpers.JsonOptions)!);
        engineReads.Should().Contain("Script", $"the engine's deserialiser reads the block behind the second casing ({shape})");

        var collected = WorkflowDefinitionBlockTypes.Collect(json);

        collected.Should().Contain(engineReads, shape);
        collected.Should().Contain(["Log", "Script"], "every casing is read, whichever one a deserialiser keeps");
    }

    [Fact]
    public void An_id_whose_escape_does_not_decode_throws_rather_than_answering()
    {
        const string json = """{"nodes":[{"id":"a","blockTypeId":"Log","BlockTypeId":"\uDC00"}],"edges":[]}""";

        var act = () => WorkflowDefinitionBlockTypes.Collect(json);

        act.Should().Throw<InvalidOperationException>("an answer would name less than the text holds");
    }

    [Fact]
    public void Text_holding_a_raw_unpaired_surrogate_throws_rather_than_answering()
    {
        var json = "{\"nodes\":[{\"id\":\"a\",\"blockTypeId\":\"Log" + '\uD800' + "\"}],\"edges\":[]}";

        var act = () => WorkflowDefinitionBlockTypes.Collect(json);

        act.Should().Throw<ArgumentException>().WithMessage("*UTF-16*");
    }

    [Fact]
    public void Text_that_is_not_json_names_no_blocks()
    {
        WorkflowDefinitionBlockTypes.Collect("{\"nodes\":[").Should().BeEmpty();
    }

    private static IEnumerable<string> BlockTypeIds(WorkflowDefinition definition) =>
        definition.Nodes.SelectMany(node =>
            (node.SubCanvases?.Values.SelectMany(BlockTypeIds) ?? []).Prepend(node.BlockTypeId));
}
