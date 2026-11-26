using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class BlockShapeTests
{
    [Fact]
    public void Registration_refuses_a_dotted_port_name_with_the_named_rule()
    {
        var act = () => BlockShape.From(new FakeFactory(portName: "result.value"));
        act.Should().Throw<ArgumentException>().WithMessage("*dotted-name*");
    }
    [Fact]
    public void A_port_shape_refuses_a_default_type_however_it_is_given_one()
    {
        var constructed = () => new PortShape("p", default, false);
        var copied = () => new PortShape("p", PortValueType.Int, false) with { Type = default };

        constructed.Should().Throw<ArgumentException>().WithMessage("*default(PortValueType)*").WithParameterName("Type");
        copied.Should().Throw<ArgumentException>().WithMessage("*default(PortValueType)*").WithParameterName("Type");
    }

    [Fact]
    public void A_shape_reads_a_factorys_ports_with_their_declared_types()
    {
        var shape = BlockShape.From(new FakeFactory());

        shape.BlockTypeId.Should().Be("fake.block");
        shape.Inputs.Should().ContainSingle(p => p.Name == "Target" && p.Type == PortValueType.Of("Shipment") && p.IsRequired);
        shape.Outputs.Should().ContainSingle(p => p.Name == "Result" && p.Type == PortValueType.Number);
    }

    [Fact]
    public void A_shape_carries_the_hosts_provenance_without_reading_it()
    {
        var shape = BlockShape.From(new FakeFactory());

        shape.Source.Should().Be("reeve-plugin-bt@2.1.0");
    }

    [Fact]
    public void A_shape_reads_CanSuspend_onto_its_own_flag_and_no_other()
    {
        var shape = BlockShape.From(new FakeFactory(canSuspend: true));

        shape.Flags.Should().Be(new BlockFlags(
            HasDynamicPorts: false, SupportsDeclaredOutputs: false, CanSuspend: true,
            OnlyDrivesItsSubCanvases: false, PortsAreResolvedElsewhere: false));
    }

    [Fact]
    public void A_shape_reads_OnlyDrivesItsSubCanvases_onto_its_own_flag_and_no_other()
    {
        var shape = BlockShape.From(new FakeFactory(onlyDrivesItsSubCanvases: true));

        shape.Flags.Should().Be(new BlockFlags(
            HasDynamicPorts: false, SupportsDeclaredOutputs: false, CanSuspend: false,
            OnlyDrivesItsSubCanvases: true, PortsAreResolvedElsewhere: false));
    }

    [Fact]
    public void A_shape_reads_HasDynamicPorts_onto_its_own_flag_and_no_other()
    {
        var shape = BlockShape.From(new FakeFactory(hasDynamicPorts: true));

        shape.Flags.Should().Be(new BlockFlags(
            HasDynamicPorts: true, SupportsDeclaredOutputs: false, CanSuspend: false,
            OnlyDrivesItsSubCanvases: false, PortsAreResolvedElsewhere: false));
    }

    [Fact]
    public void A_shape_reads_SupportsDeclaredOutputs_onto_its_own_flag_and_no_other()
    {
        var shape = BlockShape.From(new FakeFactory(supportsDeclaredOutputs: true));

        shape.Flags.Should().Be(new BlockFlags(
            HasDynamicPorts: false, SupportsDeclaredOutputs: true, CanSuspend: false,
            OnlyDrivesItsSubCanvases: false, PortsAreResolvedElsewhere: false));
    }

    [Fact]
    public void A_shape_reads_PortsAreResolvedElsewhere_onto_its_own_flag_and_no_other()
    {
        var shape = BlockShape.From(new FakeFactory(portsAreResolvedElsewhere: true));

        shape.Flags.Should().Be(new BlockFlags(
            HasDynamicPorts: false, SupportsDeclaredOutputs: false, CanSuspend: false,
            OnlyDrivesItsSubCanvases: false, PortsAreResolvedElsewhere: true));
    }

    [Fact]
    public void A_shape_reads_a_factorys_workflow_reference_input_name()
    {
        var shape = BlockShape.From(new FakeFactory(runsWorkflowFrom: "SomeReference"));

        shape.RunsWorkflowFrom.Should().Be("SomeReference");
    }

    [Fact]
    public void A_shape_reads_a_factorys_configuration_schema()
    {
        var shape = BlockShape.From(new FakeFactory(
            configFields: [new("Threshold", "Threshold", ConfigFieldType.Number, true)]));

        shape.ConfigurationFields.Should().ContainSingle(f =>
            f.Name == "Threshold" && f.Type == ConfigFieldType.Number && f.Required);
    }

    [Fact]
    public void A_shape_reads_a_factorys_sub_canvas_names()
    {
        var shape = BlockShape.From(new FakeFactory(subCanvasNames: ["Body"]));

        shape.SubCanvasNames.Should().ContainSingle(n => n == "Body");
    }

    [Fact]
    public void A_factory_declaring_the_same_sub_canvas_name_twice_is_refused_by_name()
    {
        var act = () => BlockShape.From(new FakeFactory(subCanvasNames: ["Body", "Body"]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'Body'*");
    }

    [Fact]
    public void Mutating_a_callers_port_list_after_construction_does_not_change_the_shape()
    {
        var callerOwned = new List<PortShape> { new("a", PortValueType.String, true) };
        var noSub = new Dictionary<string, IReadOnlyList<PortShape>>();
        var shape = new BlockShape("box", callerOwned, [], [], default, noSub, noSub, null);

        callerOwned.Add(new PortShape("b", PortValueType.Number, false));

        shape.Inputs.Should().HaveCount(1).And.OnlyContain(p => p.Name == "a");
    }

    [Fact]
    public void Mutating_a_callers_config_field_list_after_construction_does_not_change_the_shape()
    {
        var callerOwned = new List<ConfigFieldShape> { new("a", ConfigFieldType.String, true) };
        var noSub = new Dictionary<string, IReadOnlyList<PortShape>>();
        var shape = new BlockShape("box", [], [], callerOwned, default, noSub, noSub, null);

        callerOwned.Add(new ConfigFieldShape("b", ConfigFieldType.Number, false));

        shape.ConfigurationFields.Should().HaveCount(1).And.OnlyContain(f => f.Name == "a");
    }

    [Fact]
    public void Mutating_a_callers_sub_canvas_port_list_after_construction_does_not_change_the_shape()
    {
        var innerList = new List<PortShape> { new("a", PortValueType.String, true) };
        var subOut = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = innerList };
        var shape = new BlockShape("box", [], [], [], default,
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] }, subOut, null, SubCanvasNames: ["Body"]);

        innerList.Add(new PortShape("b", PortValueType.Number, false));

        shape.SubCanvasOutputs["Body"].Should().HaveCount(1).And.OnlyContain(p => p.Name == "a");
    }

    [Fact]
    public void Mutating_a_callers_sub_canvas_names_list_after_construction_does_not_change_the_shape()
    {
        var callerOwned = new List<string> { "Body" };
        var subOut = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] };
        var shape = new BlockShape("box", [], [], [], default,
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] }, subOut, null, SubCanvasNames: callerOwned);

        callerOwned.Add("Extra");

        shape.SubCanvasNames.Should().Equal("Body");
    }

    [Fact]
    public void A_callers_downcast_of_outputs_cannot_mutate_it_in_place()
    {
        var shape = new BlockShape("box", [], [new PortShape("a", PortValueType.String, true)], [], default,
            new Dictionary<string, IReadOnlyList<PortShape>>(), new Dictionary<string, IReadOnlyList<PortShape>>(), null);
        var asList = (IList<PortShape>)shape.Outputs;

        var act = () => asList[0] = new PortShape("b", PortValueType.String, true);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_callers_downcast_of_sub_canvas_outputs_cannot_mutate_it_in_place()
    {
        var subOut = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] };
        var shape = new BlockShape("box", [], [], [], default,
            subOut, subOut, null, SubCanvasNames: ["Body"]);
        var asDict = (IDictionary<string, IReadOnlyList<PortShape>>)shape.SubCanvasOutputs;

        var act = () => asDict.Add("Extra", []);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_sub_canvas_map_key_absent_from_sub_canvas_names_is_refused_not_silently_unhashed()
    {
        var withX = new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["X"] = [new PortShape("Out", PortValueType.String, false)],
        };
        var act = () => new BlockShape("qual", [],
            [new PortShape("Picked", PortValueType.String, false)],
            [], default, new Dictionary<string, IReadOnlyList<PortShape>>(), withX, null, SubCanvasNames: []);

        act.Should().Throw<ArgumentException>().WithMessage("*'X'*SubCanvasNames*");
    }

    [Fact]
    public void A_sub_canvas_input_map_key_absent_from_sub_canvas_names_is_refused_too()
    {
        var withX = new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["X"] = [new PortShape("In", PortValueType.String, false)],
        };
        var act = () => new BlockShape("qual", [], [], [], default,
            withX, new Dictionary<string, IReadOnlyList<PortShape>>(), null, SubCanvasNames: []);

        act.Should().Throw<ArgumentException>().WithMessage("*'X'*SubCanvasNames*");
    }

    [Fact]
    public void A_sub_canvas_map_key_differing_from_its_declared_name_only_by_case_is_refused()
    {
        var outputs = new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Then"] = [],
            ["then"] = [new PortShape("Out", PortValueType.String, false)],
        };
        var inputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Then"] = [] };
        var act = () => new BlockShape("box", [], [], [], default, inputs, outputs, null, SubCanvasNames: ["Then"]);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasOutputs").WithMessage("*'then'*");
    }

    [Fact]
    public void Two_case_variant_sub_canvas_names_keep_their_own_distinct_ports()
    {
        var outputs = new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Then"] = [new PortShape("A", PortValueType.String, false)],
            ["then"] = [new PortShape("B", PortValueType.Number, false)],
        };
        var inputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Then"] = [], ["then"] = [] };
        var shape = new BlockShape("box", [], [], [], default, inputs, outputs, null, SubCanvasNames: ["Then", "then"]);

        shape.SubCanvasOutputs["Then"].Should().ContainSingle(p => p.Name == "A");
        shape.SubCanvasOutputs["then"].Should().ContainSingle(p => p.Name == "B");
    }

    [Fact]
    public void A_factory_whose_sub_canvas_names_differ_between_reads_no_longer_breaks_From()
    {
        BlockShape? shape = null;
        var act = () => shape = BlockShape.From(new FlippingSubCanvasNamesFactory());

        act.Should().NotThrow();
        shape!.SubCanvasNames.Should().Equal("Body");
    }

    [Fact]
    public void A_null_entry_in_sub_canvas_names_is_refused_with_the_documented_message()
    {
        var noSub = new Dictionary<string, IReadOnlyList<PortShape>>();
        var act = () => new BlockShape("box", [], [], [], default, noSub, noSub, null, SubCanvasNames: [null!]);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasNames").WithMessage("*null entry*");
    }

    [Fact]
    public void A_duplicate_sub_canvas_name_given_directly_to_the_constructor_is_refused()
    {
        var outputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] };
        var act = () => new BlockShape("box", [], [], [], default, outputs, outputs, null, SubCanvasNames: ["Body", "Body"]);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasNames").WithMessage("*'Body'*more than once*");
    }

    [Fact]
    public void A_declared_sub_canvas_name_missing_from_inputs_is_refused()
    {
        var outputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] };
        var noInputs = new Dictionary<string, IReadOnlyList<PortShape>>();
        var act = () => new BlockShape("box", [], [], [], default, noInputs, outputs, null, SubCanvasNames: ["Body"]);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasInputs").WithMessage("*'Body'*");
    }

    [Fact]
    public void A_declared_sub_canvas_name_missing_from_outputs_is_refused()
    {
        var inputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] };
        var noOutputs = new Dictionary<string, IReadOnlyList<PortShape>>();
        var act = () => new BlockShape("box", [], [], [], default, inputs, noOutputs, null, SubCanvasNames: ["Body"]);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasOutputs").WithMessage("*'Body'*");
    }

    private sealed class FlippingSubCanvasNamesFactory : IWorkflowBlockFactory
    {
        private int reads;
        public string BlockTypeId => "flip.block";
        public string DisplayName => "Flip";
        public string Description => "";
        public string Category => "test";
        public bool OnlyDrivesItsSubCanvases => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

        public IReadOnlyCollection<string> SubCanvasNames => reads++ == 0 ? ["Body"] : ["Body", "Extra"];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class FakeFactory(
        bool hasDynamicPorts = false,
        bool supportsDeclaredOutputs = false,
        bool canSuspend = false,
        bool onlyDrivesItsSubCanvases = false,
        bool portsAreResolvedElsewhere = false,
        string? runsWorkflowFrom = null,
        IReadOnlyCollection<BlockConfigFieldDefinition>? configFields = null,
        IReadOnlyCollection<string>? subCanvasNames = null,
        string portName = "Target") : IWorkflowBlockFactory
    {
        public string BlockTypeId => "fake.block";
        public string DisplayName => "Fake";
        public string Description => "";
        public string Category => "test";
        public string? Source => "reeve-plugin-bt@2.1.0";
        public bool HasDynamicPorts => hasDynamicPorts;
        public bool SupportsDeclaredOutputs => supportsDeclaredOutputs;
        public bool CanSuspend => canSuspend;
        public bool OnlyDrivesItsSubCanvases => onlyDrivesItsSubCanvases;
        public bool PortsAreResolvedElsewhere => portsAreResolvedElsewhere;
        public string? RunsWorkflowFrom => runsWorkflowFrom;
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => configFields ?? [];
        public IReadOnlyCollection<string> SubCanvasNames => subCanvasNames ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new(portName, portName, PortValueType.Of("Shipment"), true)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new("Result", "Result", PortValueType.Number, false)];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
