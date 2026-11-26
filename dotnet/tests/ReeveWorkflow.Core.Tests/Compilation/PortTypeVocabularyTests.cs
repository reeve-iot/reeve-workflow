using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PortTypeVocabularyTests
{
    private static CompilationResult CompileDynamicOutput(string type, CompilationOptions? options = null)
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = $$"""[{"name":"P","type":"{{type}}"}]""" })],
            [Edge("e1", "start", "a")]);
        return WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn.source", hasDynamicPorts: true)), CanvasSignature.Empty, options);
    }

    private static string[] Vocabulary(CompilationResult result) => [.. result.Diagnostics
        .Where(d => d.RuleId is CompilationRules.PortTypeAlias or CompilationRules.PortTypeUnregistered)
        .Select(d => d.RuleId)];

    [Theory]
    [InlineData("String")]
    [InlineData("Int")]
    [InlineData("Collection<Int>")]
    [InlineData("Collection<Collection<Json>>")]
    public void A_canonical_registered_name_is_accepted(string type) =>
        Vocabulary(CompileDynamicOutput(type)).Should().BeEmpty();

    [Theory]
    [InlineData("string")]
    [InlineData("STRING")]
    [InlineData("bool")]
    [InlineData("Collection<boolean>")]
    public void An_alias_or_case_variant_of_a_registered_name_is_port_type_alias(string type)
    {
        var result = CompileDynamicOutput(type);

        result.Succeeded.Should().BeFalse();
        Vocabulary(result).Should().Equal(CompilationRules.PortTypeAlias);
        result.Diagnostics.Single(d => d.RuleId == CompilationRules.PortTypeAlias).NodeId.Should().Be("a");
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("Collection<Any>")]
    [InlineData("Collection<Collection<any>>")]
    [InlineData("Float")]
    [InlineData("float")]
    [InlineData("Collection<Float>")]
    [InlineData("Any?")]
    [InlineData("Collection<Float?>")]
    [InlineData("Gauge<Any>")]
    public void Any_and_Float_in_a_stored_type_are_port_type_alias(string type)
    {
        var result = CompileDynamicOutput(type);

        result.Succeeded.Should().BeFalse();
        Vocabulary(result).Should().Equal(CompilationRules.PortTypeAlias);
        result.Diagnostics.Single(d => d.RuleId == CompilationRules.PortTypeAlias).NodeId.Should().Be("a");
    }

    [Theory]
    [InlineData("Shipment")]
    [InlineData("Collection<Shipment>")]
    [InlineData("Gauge<Boolean>")]
    [InlineData("List<Device>")]
    [InlineData("Collection<Shipment?>")]
    public void A_well_formed_name_nobody_registered_is_port_type_unregistered(string type)
    {
        var result = CompileDynamicOutput(type);

        result.Succeeded.Should().BeFalse();
        Vocabulary(result).Should().Equal(CompilationRules.PortTypeUnregistered);
    }

    [Theory]
    [InlineData("Int?")]
    [InlineData("Collection<Int?>")]
    [InlineData("Collection<Int>?")]
    public void A_nullable_term_of_registered_types_is_registered(string type)
    {
        Vocabulary(CompileDynamicOutput(type)).Should().BeEmpty();
    }

    private static CompilationOptions Shipping(WorkflowTypeRecord record) =>
        new() { TypeRegistry = new WorkflowTypeRegistry([record]) };

    [Fact]
    public void A_host_that_declares_the_name_accepts_it_and_a_host_alias_is_an_alias()
    {
        var options = Shipping(new WorkflowTypeRecord("Shipment", typeof(string)) { Aliases = ["Parcel"] });

        Vocabulary(CompileDynamicOutput("Shipment", options)).Should().BeEmpty();
        Vocabulary(CompileDynamicOutput("Parcel", options)).Should().Equal(CompilationRules.PortTypeAlias);
    }

    [Theory]
    [InlineData("shipment")]
    [InlineData("SHIPMENT")]
    [InlineData("Collection<shipment>")]
    [InlineData("Collection<Collection<SHIPMENT>>")]
    public void A_case_variant_of_a_host_declared_name_is_port_type_alias(string type)
    {
        var options = Shipping(new WorkflowTypeRecord("Shipment", typeof(string)));

        Vocabulary(CompileDynamicOutput(type, options)).Should().Equal(CompilationRules.PortTypeAlias);
    }

    [Fact]
    public void A_declared_name_canonicalises_to_its_declared_spelling_and_a_second_pass_changes_nothing()
    {
        var table = PortTypeWideningTable.Builtin
            .Declaring(PortValueType.Of("Shipment"))
            .With(PortValueType.Of("Parcel"), [PortValueType.Of("Shipment")]);

        table.Canonicalize(PortValueType.Of("sHiPmEnT")).Name.Should().Be("Shipment");
        table.Canonicalize(PortValueType.Of("Collection<PARCEL>")).Name.Should().Be("Collection<Parcel>");
        table.Canonicalize(PortValueType.Of("Shipment")).Name.Should().Be("Shipment");
        table.IsCanonical(PortValueType.Of("shipment")).Should().BeFalse();
        table.IsCanonical(PortValueType.Of("Shipment")).Should().BeTrue();
        table.IsAssignable(PortValueType.Of("PARCEL"), PortValueType.Of("shipment")).Should().BeTrue("case never changed what a name means");
    }

    [Fact]
    public void A_malformed_name_is_port_type_malformed_alone()
    {
        var result = CompileDynamicOutput("Collection<");

        result.Diagnostics.Select(d => d.RuleId).Should().Equal(CompilationRules.PortTypeMalformed);
    }

    [Theory]
    [InlineData("""[{"name":"in","type":"string"}]""", null, CompilationRules.PortTypeAlias)]
    [InlineData(null, """[{"name":"out","type":"Shipment"}]""", CompilationRules.PortTypeUnregistered)]
    public void A_workflow_parameter_is_spelled_against_the_vocabulary_too(string? inputs, string? outputs, string rule)
    {
        CanvasSignature.TryRead(inputs, outputs, out var signature, out _).Should().BeTrue();

        var result = WorkflowCompiler.Compile(
            Graph([StartNode()]), Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), signature!);

        var diagnostic = result.Diagnostics.Should().ContainSingle(d => d.RuleId == rule).Subject;
        diagnostic.NodeId.Should().BeNull();
    }

    [Theory]
    [InlineData("Int?")]
    [InlineData("Collection<Int>?")]
    [InlineData("Gauge<Boolean>")]
    public void A_nullable_or_constructor_name_cannot_be_registered_so_it_stays_unregistered(string name)
    {
        var term = PortValueType.Of(name);

        ((Action)(() => PortTypeWideningTable.Builtin.Declaring(term))).Should().Throw<ArgumentException>()
            .WithMessage("*only a plain name can be declared*");
        ((Action)(() => PortTypeWideningTable.Builtin.With(PortValueType.Int, [term]))).Should().Throw<ArgumentException>()
            .WithMessage("*only a plain name can be declared*");
        ((Action)(() => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Maybe"), term))).Should().Throw<ArgumentException>()
            .WithMessage("*only a plain name can be declared*");
        ((Action)(() => PortTypeWideningTable.Builtin.WithAlias(term, PortValueType.Int))).Should().Throw<ArgumentException>()
            .WithMessage("*only a plain name can be an alias*");
        PortTypeWideningTable.Builtin.IsRegistered(term).Should().BeFalse();
    }

    [Theory]
    [InlineData("Collection<Int>?", 0, "Collection<Int>?")]
    [InlineData("Collection<Collection<Int>?>", 1, "Collection<Int>?")]
    [InlineData("Collection<Int?>", 1, "Int?")]
    [InlineData("Collection<Collection<Int>>", 2, "Int")]
    [InlineData("Gauge<Int>", 0, "Gauge<Int>")]
    [InlineData("Collection<Gauge<Int>>", 1, "Gauge<Int>")]
    public void A_nullable_level_is_not_peeled_when_a_name_is_read_as_depth_and_element(string name, int depth, string element)
    {
        var term = PortTypeTerm.From(PortValueType.Of(name));

        term.Depth.Should().Be(depth);
        term.Element.Should().Be(element);
    }
}
