using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PluginDeclarationVocabularyTests
{
    private sealed record Crate;

    private static IWorkflowTypeRegistry HostWithDevice() => new WorkflowTypeRegistry(
        [new WorkflowTypeRecord("Device", typeof(Crate)) { Aliases = ["Gadget"] }]);

    private static BlockPortDefinition Port(string type, string name = "A") =>
        new(name, name, PortValueType.OfDeclaration(type), false);

    [Theory]
    [InlineData("device", "Device")]
    [InlineData("Collection<device>", "Collection<Device>")]
    [InlineData("device?", "Device?")]
    [InlineData("Gadget", "Device")]
    [InlineData("Collection<Gadget?>", "Collection<Device?>")]
    [InlineData("Collection<Collection<device>?>", "Collection<Collection<Device>?>")]
    public void A_host_alias_or_case_variant_spelling_in_a_plugins_port_is_refused_naming_the_canonical_spelling(string written, string canonical)
    {
        var read = () => BlockShape.FromPlugin(new Plugin(outputs: [Port(written)]), HostWithDevice());

        read.Should().Throw<ArgumentException>()
            .WithMessage($"port-type-alias:*'plugin.block'*port 'A'*'{written}'*alias*'{canonical}'*");
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("Collection<Nope>")]
    [InlineData("Gauge<Device>")]
    public void A_type_nobody_registered_in_a_plugins_port_is_refused(string written)
    {
        var read = () => BlockShape.FromPlugin(new Plugin(inputs: [Port(written)]), HostWithDevice());

        read.Should().Throw<ArgumentException>().WithMessage($"port-type-unregistered:*'plugin.block'*port 'A'*'{written}'*");
    }

    [Fact]
    public void A_reserved_name_is_reported_before_the_vocabulary_is_asked()
    {
        var read = () => BlockShape.FromPlugin(new Plugin(inputs: [Port("Collection<Float>")]), HostWithDevice());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-alias:*Float*");
    }

    [Fact]
    public void A_declaration_in_canonical_spelling_over_registered_types_and_parameters_is_read()
    {
        var factory = new Plugin(
            inputs: [Port("Device"), Port("Collection<$T>", "B"), Port("Collection<Device?>", "C"), Port("Expression<Device>", "D")],
            outputs: [Port("$T", "Out")], typeParameters: [new WorkflowTypeParameter("T", [])]);

        var shape = BlockShape.FromPlugin(factory, HostWithDevice());

        shape.Inputs.Select(p => p.Name).Should().Equal("A", "B", "C", "D");
    }

    [Fact]
    public void An_Expression_input_is_graded_through_its_argument()
    {
        var read = () => BlockShape.FromPlugin(new Plugin(inputs: [Port("Expression<device>")]), HostWithDevice());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-alias:*port 'A'*'device'*'Device'*");
    }

    [Fact]
    public void A_sub_canvas_port_is_graded_like_any_other()
    {
        var factory = new Plugin(subCanvasOutputs: [Port("Collection<Nope>", "Item")]);

        var read = () => BlockShape.FromPlugin(factory, HostWithDevice());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-unregistered:*'plugin.block'*sub-canvas 'Body' port 'Item'*");
    }

    [Fact]
    public void A_type_the_plugin_registered_before_its_blocks_are_read_is_live()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:gauges", [new WorkflowTypeRecord("Gauge", typeof(Crate)) { Aliases = ["Meter"] }]).Refusals.Should().BeEmpty();

        BlockShape.FromPlugin(new Plugin(inputs: [Port("Gauge")]), registry).Inputs.Should().ContainSingle();
        var aliased = () => BlockShape.FromPlugin(new Plugin(inputs: [Port("Meter")]), registry);
        aliased.Should().Throw<ArgumentException>().WithMessage("port-type-alias:*'Meter'*'Gauge'*");
    }

    [Fact]
    public void A_type_another_plugin_has_not_registered_is_refused_and_the_block_is_not_held()
    {
        var registry = new WorkflowTypeRegistry();
        var factory = new Plugin(inputs: [Port("Gauge")]);
        var early = () => BlockShape.FromPlugin(factory, registry);

        early.Should().Throw<ArgumentException>().WithMessage("port-type-unregistered:*'Gauge'*");

        registry.Register("plugin:gauges", [new WorkflowTypeRecord("Gauge", typeof(Crate))]);
        BlockShape.FromPlugin(factory, registry).Inputs.Should().ContainSingle("a later load reads the block afresh");

        registry.Unregister("plugin:gauges");
        early.Should().Throw<ArgumentException>().WithMessage("port-type-unregistered:*'Gauge'*",
            "a fresh load reads it against the registry as it is now");
    }

    private static IWorkflowTypeRegistry HostWithGaugeOverInt() => new WorkflowTypeRegistry(
        [new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Int"] }]);

    [Fact]
    public void An_argument_a_host_constructor_does_not_admit_is_refused()
    {
        var read = () => BlockShape.FromPlugin(new Plugin(inputs: [Port("Gauge<String>")]), HostWithGaugeOverInt());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-unregistered:*'plugin.block'*port 'A'*'Gauge<String>'*does not admit*");
        BlockShape.FromPlugin(new Plugin(inputs: [Port("Gauge<Int>")]), HostWithGaugeOverInt()).Inputs.Should().ContainSingle();
    }

    [Fact]
    public void A_host_constructor_over_a_type_parameter_is_read()
    {
        var factory = new Plugin(inputs: [Port("Gauge<$T>")], typeParameters: [new WorkflowTypeParameter("T", [])]);

        BlockShape.FromPlugin(factory, HostWithGaugeOverInt()).Inputs.Should().ContainSingle();
    }

    [Fact]
    public void A_nullable_type_parameter_is_read()
    {
        var factory = new Plugin(
            inputs: [Port("Collection<$T>")], outputs: [Port("$T?", "Out")], typeParameters: [new WorkflowTypeParameter("T", [])]);

        BlockShape.FromPlugin(factory, HostWithDevice()).Outputs.Should().ContainSingle();
    }

    [Fact]
    public void A_sub_canvas_input_is_graded_like_any_other()
    {
        var factory = new Plugin(subCanvasInputs: [Port("device", "Item")], subCanvasOutputs: []);

        var read = () => BlockShape.FromPlugin(factory, HostWithDevice());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-alias:*'plugin.block'*sub-canvas 'Body' port 'Item'*'device'*'Device'*");
    }

    [Fact]
    public void A_plugin_is_read_against_a_registry_and_never_without_one()
    {
        var read = () => BlockShape.FromPlugin(new Plugin(), null!);

        read.Should().Throw<ArgumentNullException>();
    }

    private sealed class Plugin(
        BlockPortDefinition[]? inputs = null, BlockPortDefinition[]? outputs = null, BlockPortDefinition[]? subCanvasOutputs = null, BlockPortDefinition[]? subCanvasInputs = null,
        WorkflowTypeParameter[]? typeParameters = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "plugin.block";
        public string DisplayName => BlockTypeId;
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public IReadOnlyCollection<string> SubCanvasNames => subCanvasOutputs is null && subCanvasInputs is null ? [] : ["Body"];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string name) => subCanvasInputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string name) => subCanvasOutputs ?? [];
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => typeParameters ?? [];
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
