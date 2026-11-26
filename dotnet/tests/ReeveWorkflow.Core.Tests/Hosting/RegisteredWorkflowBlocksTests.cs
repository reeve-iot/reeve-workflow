using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Hosting;

public class RegisteredWorkflowBlocksTests
{
    private sealed class StubFactory(string blockTypeId, bool privileged = false, string? dottedSide = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId { get; } = blockTypeId;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<string> SubCanvasNames => dottedSide is "branch-input" or "branch-output" ? ["Body"] : [];
        public bool IsPrivileged { get; } = privileged;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => dottedSide == "input"
            ? [new("result.value", "Result", PortValueType.String, false)] : [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => dottedSide == "output"
            ? [new("result.value", "Result", PortValueType.String, false)] : [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string name) => dottedSide == "branch-input"
            ? [new("result.value", "Result", PortValueType.String, false)] : [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string name) => dottedSide == "branch-output"
            ? [new("result.value", "Result", PortValueType.String, false)] : [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException("Never created in these tests.");
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("branch-input")]
    [InlineData("branch-output")]
    public void Dotted_ports_are_refused_at_startup_on_every_factory_port_side(string side)
    {
        var act = () => new RegisteredWorkflowBlocks([new StubFactory("bad", dottedSide: side)]);
        act.Should().Throw<ArgumentException>().WithMessage("*result.value*dotted-name*");
    }

    private static string Definition(params string[] blockTypeIds) =>
        "{\"nodes\":[" + string.Join(",", blockTypeIds.Select(id => $"{{\"blockTypeId\":\"{id}\"}}")) + "]}";

    [Fact]
    public void Find_matches_the_id_case_insensitively()
    {
        var blocks = new RegisteredWorkflowBlocks([new StubFactory("SendEmail")]);

        blocks.Find("sendemail").Should().NotBeNull();
        blocks.Find("SendEmail").Should().NotBeNull();
        blocks.Find("SendSms").Should().BeNull();
    }

    [Fact]
    public void Two_factories_under_one_id_are_refused_at_construction()
    {
        var act = () => new RegisteredWorkflowBlocks([new StubFactory("SendEmail"), new StubFactory("sendemail")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SendEmail*");
    }

    [Theory]
    [InlineData("WorkflowInput")]
    [InlineData("WorkflowOutput")]
    [InlineData("workflowoutput")]
    public void A_factory_under_a_boundary_block_type_id_is_refused_as_reserved(string id)
    {
        var act = () => new RegisteredWorkflowBlocks([new StubFactory("Log"), new StubFactory(id)]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{id}' is reserved*");
    }

    [Fact]
    public void ContainsPrivilegedBlock_is_true_only_when_the_definition_names_a_privileged_one()
    {
        var blocks = new RegisteredWorkflowBlocks(
            [new StubFactory("Script", privileged: true), new StubFactory("Log")]);

        blocks.ContainsPrivilegedBlock(Definition("Log")).Should().BeFalse();
        blocks.ContainsPrivilegedBlock(Definition("Log", "Script")).Should().BeTrue();
    }

    [Fact]
    public void An_unregistered_id_counts_as_not_privileged()
    {
        var blocks = new RegisteredWorkflowBlocks([new StubFactory("Log")]);

        blocks.ContainsPrivilegedBlock(Definition("SomethingNobodyRegistered")).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"nodes":[{"blockTypeId":"Log"}],"Nodes":[{"blockTypeId":"Script"}],"edges":[]}""")]
    [InlineData("""{"Nodes":[{"blockTypeId":"Script"}],"nodes":[{"blockTypeId":"Log"}]}""")]
    [InlineData("""{"nodes":[{"blockTypeId":"Log","BlockTypeId":"Script"}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"BLOCKTYPEID":"Script","blockTypeId":"Log"}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"blockTypeId":"Log","subCanvases":{"Body":{"nodes":[{"blockTypeId":"Log"}],"Nodes":[{"blockTypeId":"Script"}],"edges":[]}}}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"blockTypeId":"Log","subCanvases":{},"SubCanvases":{"Body":{"nodes":[{"blockTypeId":"Script"}]}}}],"edges":[]}""")]
    public void A_privileged_block_behind_a_second_casing_of_a_key_is_found(string definition)
    {
        var blocks = new RegisteredWorkflowBlocks([new StubFactory("Script", privileged: true), new StubFactory("Log")]);

        WorkflowDefinitionBlockTypes.Collect(definition).Should().BeEquivalentTo(["Log", "Script"]);
        blocks.ContainsPrivilegedBlock(definition).Should().BeTrue();
    }

    [Fact]
    public void Two_casings_of_one_block_id_are_one_entry_that_answers_in_any_casing()
    {
        var found = WorkflowDefinitionBlockTypes.Collect(Definition("script", "Script"));

        found.Should().ContainSingle();
        found.Contains("SCRIPT").Should().BeTrue();
        new RegisteredWorkflowBlocks([new StubFactory("Script", privileged: true)]).ContainsPrivilegedBlock(Definition("script")).Should().BeTrue();
    }

    [Fact]
    public void GetAll_returns_every_registered_factory()
    {
        var blocks = new RegisteredWorkflowBlocks([new StubFactory("A"), new StubFactory("B")]);

        blocks.GetAll().Select(f => f.BlockTypeId).Should().BeEquivalentTo(["A", "B"]);
    }
}
