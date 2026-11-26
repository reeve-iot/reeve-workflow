using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class WorkflowBlockCatalogueTests
{
    private sealed class Factory(bool enumField = false) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "probe";

        public string DisplayName => "Probe";

        public string Description => "a block";

        public string Category => "Testing";

        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new("in", "In", PortValueType.String, true, ["a", "b"])];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new("out", "Out", PortValueType.Number, false)];

        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => enumField
            ? [new("kind", "Kind", ConfigFieldType.Enum, false, "Dim") { AllowedValues = ["Toggle", "Dim", "Scene"] },
                new("mode", "Mode", ConfigFieldType.Int, true, "3", "how")]
            : [new("mode", "Mode", ConfigFieldType.Int, true, "3", "how")];

        public IReadOnlyCollection<string> SubCanvasNames => ["body"];

        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
            subCanvasName == "body" ? [new("item", "Item", PortValueType.String, false)] : [];

        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
            subCanvasName == "body" ? [new("done", "Done", PortValueType.Boolean, true)] : [];

        public bool HasDynamicPorts => true;

        public bool SupportsDeclaredOutputs => false;

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException("described, never run");
    }

    private static WorkflowBlockTypeResponse Described => WorkflowBlockDescription.Describe(new Factory());

    [Fact]
    public void A_ports_allowed_values_survive_the_projection()
    {
        Described.Inputs.Single().AllowedValues.Should().Equal(["a", "b"],
            because: "the allowed values ARE the picker: a port that loses them is edited as a free-text "
                     + "box, and the block receives a value it declared it would never receive");
    }

    [Fact]
    public void An_enum_field_is_described_by_its_type_and_its_names_in_declared_order_and_other_fields_carry_none()
    {
        var fields = WorkflowBlockDescription.Describe(new Factory(enumField: true)).ConfigurationSchema.ToDictionary(f => f.Name);

        fields["kind"].Type.Should().Be("Enum");
        fields["kind"].AllowedValues.Should().Equal("Toggle", "Dim", "Scene");
        fields["mode"].AllowedValues.Should().BeNull();
    }

    [Fact]
    public void A_port_type_is_named_rather_than_numbered()
    {
        Described.Inputs.Single().Type.Should().Be("String");
        Described.Outputs.Single().Type.Should().Be("Number");
        Described.ConfigurationSchema.Single().Type.Should().Be("Int");
    }

    [Fact]
    public void Both_sides_of_a_sub_canvas_are_keyed_by_its_name()
    {
        Described.SubCanvasNames.Should().Equal("body");
        Described.SubCanvasInputs["body"].Single().Name.Should().Be("item");
        Described.SubCanvasOutputs["body"].Single().Name.Should().Be("done",
            because: "the exit anchor draws these, and a branch whose outputs went missing offers nothing "
                     + "to wire the value its parent promised");
    }

    [Fact]
    public void The_two_capability_flags_are_carried_from_the_factory_and_not_transposed()
    {
        Described.HasDynamicPorts.Should().BeTrue();
        Described.SupportsDeclaredOutputs.Should().BeFalse();
        Described.BlockTypeId.Should().Be("probe");
        Described.Category.Should().Be("Testing");
    }
}
