using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting.Defaults;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class EnumConfigFieldTests
{
    private static readonly string[] Names = ["Toggle", "Dim", "Scene"];

    private static BlockConfigFieldDefinition Field(
        ConfigFieldType type = ConfigFieldType.Enum, IReadOnlyList<string>? names = null, string? defaultValue = null) =>
        new("Kind", "Kind", type, false, defaultValue) { AllowedValues = names };

    [Theory]
    [InlineData("none", "enum-field-no-values")]
    [InlineData("empty", "enum-field-no-values")]
    [InlineData("duplicate", "enum-field-duplicate-value")]
    [InlineData("case-differs-is-not-a-duplicate", null)]
    [InlineData("empty-name", "enum-field-blank-value")]
    [InlineData("whitespace-name", "enum-field-blank-value")]
    [InlineData("names-on-string", "enum-values-on-non-enum-field")]
    [InlineData("empty-list-on-string", "enum-values-on-non-enum-field")]
    [InlineData("default-outside", "enum-field-default-not-allowed")]
    [InlineData("default-differs-in-case", "enum-field-default-not-allowed")]
    [InlineData("default-inside", null)]
    [InlineData("well-formed", null)]
    public void Registration_refuses_a_malformed_enum_declaration_under_its_own_rule(string declaration, string? rule)
    {
        var field = declaration switch
        {
            "none" => Field(names: null),
            "empty" => Field(names: []),
            "duplicate" => Field(names: ["A", "B", "A"]),
            "case-differs-is-not-a-duplicate" => Field(names: ["A", "a"]),
            "empty-name" => Field(names: ["A", ""]),
            "whitespace-name" => Field(names: ["A", "  "]),
            "names-on-string" => Field(ConfigFieldType.String, ["A"]),
            "empty-list-on-string" => Field(ConfigFieldType.String, []),
            "default-outside" => Field(names: Names, defaultValue: "Other"),
            "default-differs-in-case" => Field(names: Names, defaultValue: "toggle"),
            "default-inside" => Field(names: Names, defaultValue: "Dim"),
            _ => Field(names: Names),
        };

        var act = () => new RegisteredWorkflowBlocks([new Factory(field)]);

        if (rule is null)
        {
            act.Should().NotThrow();
            return;
        }

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain($"(rule {rule})").And.Contain("'Kind'");
        var viaShape = () => BlockShape.From(new Factory(field));
        viaShape.Should().Throw<ArgumentException>().Which.Message.Should().Contain($"(rule {rule})");
    }

    [Theory]
    [InlineData("\"Toggle\"", true)]
    [InlineData("\"Scene\"", true)]
    [InlineData("1", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("{}", false)]
    [InlineData("[\"Toggle\"]", false)]
    [InlineData("\"Other\"", false)]
    [InlineData("\"toggle\"", false)]
    [InlineData("\" Toggle\"", false)]
    [InlineData("\"\"", true)]
    [InlineData("null", true)]
    [InlineData("\"expr:\\\"Anything\\\"\"", true)]
    public void A_configuration_literal_on_an_enum_field_must_be_one_of_the_names(string json, bool accepted)
    {
        var value = JsonDocument.Parse(json).RootElement.Clone();
        var graph = Graph([StartNode(), Node("n", "setter", new Dictionary<string, object?> { ["Kind"] = value })], [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(ReeveWorkflow.Core.Model.WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("setter", configFields: [("Kind", ConfigFieldType.Enum, false)], enumValues: Names)), CanvasSignature.Empty);

        var refused = result.Diagnostics.Where(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).ToList();
        refused.Any().Should().Be(!accepted, json);
        if (!accepted)
        {
            refused.Should().ContainSingle().Which.Message.Should().Contain("'Kind'")
                .And.Contain("'Toggle', 'Dim', 'Scene'", "the message lists the allowed names, in order");
            refused[0].NodeId.Should().Be("n");
            refused[0].PortName.Should().Be("Kind");
        }
    }

    [Fact]
    public void An_enum_field_with_no_value_is_missing_when_required_exactly_as_a_string_field_is()
    {
        foreach (var type in new[] { ConfigFieldType.String, ConfigFieldType.Enum })
        {
            var graph = Graph([StartNode(), Node("n", "setter")], [Edge("e", "start", "n")]);

            var result = WorkflowCompiler.Compile(graph, Catalogue(
                Shape(ReeveWorkflow.Core.Model.WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("setter", configFields: [("Kind", type, true)], enumValues: Names)), CanvasSignature.Empty);

            result.Diagnostics.Select(d => d.RuleId).Should().Equal([CompilationRules.RequiredConfigMissing], type.ToString());
        }
    }

    [Fact]
    public void An_empty_text_on_a_required_enum_field_is_missing_and_nothing_else()
    {
        var graph = Graph([StartNode(), Node("n", "setter", new { Kind = "" })], [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(ReeveWorkflow.Core.Model.WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("setter", configFields: [("Kind", ConfigFieldType.Enum, true)], enumValues: Names)), CanvasSignature.Empty);

        result.Diagnostics.Select(d => d.RuleId).Should().Equal([CompilationRules.RequiredConfigMissing]);
    }

    [Theory]
    [InlineData("\"Dim\"", true)]
    [InlineData("\"Bogus\"", false)]
    [InlineData("\"dim\"", false)]
    [InlineData("\"\"", true)]
    [InlineData("1", false)]
    public void A_wire_literal_into_a_port_named_like_an_enum_field_must_be_one_of_the_names(string json, bool accepted)
    {
        var value = JsonDocument.Parse(json).RootElement.Clone();
        var graph = Graph([StartNode(), Node("n", "setter")], [LiteralEdge("e", "start", "n", "Kind", value)]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(ReeveWorkflow.Core.Model.WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("setter", inputs: [("Kind", PortValueType.String, false)], configFields: [("Kind", ConfigFieldType.Enum, false)], enumValues: Names)),
            CanvasSignature.Empty);

        var refused = result.Diagnostics.Where(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).ToList();
        refused.Any().Should().Be(!accepted, json);
    }

    [Fact]
    public void The_shape_a_factory_gives_carries_the_names_in_declared_order()
    {
        var shape = BlockShape.From(new Factory(Field(names: Names)));

        shape.ConfigurationFields.Should().ContainSingle().Which.AllowedValues.Should().Equal(Names);
    }

    private sealed class Factory(BlockConfigFieldDefinition declared) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "enum.setter";
        public string DisplayName => "Setter";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => [declared];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
