using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class BlockDeclarationTests
{
    internal static readonly Lazy<Dictionary<string, JsonElement>> Rows = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.RegistrationAgreementPath()));
        return document.RootElement.GetProperty("declarations").EnumerateArray()
            .ToDictionary(row => row.GetProperty("name").GetString()!, row => row.Clone());
    });

    public static TheoryData<string> Vectors() => [.. Rows.Value.Keys];

    public static TheoryData<string> PluginVectors() =>
        [.. Rows.Value.Where(r => r.Value.TryGetProperty("expectPlugin", out _)).Select(r => r.Key)];

    [Theory]
    [MemberData(nameof(Vectors))]
    public void A_declaration_is_admitted_or_refused_as_the_shared_vector_says(string name)
    {
        var row = Rows.Value[name];
        var expect = row.GetProperty("expect");
        if (row.TryGetProperty("expectDotnet", out var dotnet) && dotnet.GetString() == "unrepresentable")
        {
            var build = () => new DeclaredFactory(row);
            build.Should().Throw<PortTypeSyntaxException>(name);
            return;
        }

        var factory = new DeclaredFactory(row);
        var read = () => BlockShape.From(factory);
        if (expect.ValueKind == JsonValueKind.True)
        {
            read.Should().NotThrow(name);
            return;
        }

        var refusal = read.Should().Throw<ArgumentException>(name).Which.Message;
        refusal.Should().StartWith(expect.GetString() + ":", name);
        refusal.Should().Contain($"'{name}'", "the refusal names the block");
        foreach (var named in Names(row))
            refusal.Should().Contain(named, $"{name}: the refusal names it");
    }

    [Theory]
    [MemberData(nameof(PluginVectors))]
    public void A_plugins_declaration_naming_a_reserved_type_is_refused_as_the_vector_says(string name)
    {
        var row = Rows.Value[name];
        var factory = new DeclaredFactory(row);

        var read = () => BlockShape.FromPlugin(factory, new WorkflowTypeRegistry());

        var refusal = read.Should().Throw<ArgumentException>(name).Which.Message;
        refusal.Should().StartWith(row.GetProperty("expectPlugin").GetString() + ":");
        refusal.Should().Contain($"'{name}'");
        foreach (var named in Names(row))
            refusal.Should().Contain(named, $"{name}: the refusal names it");
    }

    [Fact]
    public void A_plugins_declaration_is_read_by_every_rule_From_applies()
    {
        var factory = new DeclaredFactory(Rows.Value["json-input"]);

        var read = () => BlockShape.FromPlugin(factory, new WorkflowTypeRegistry());

        read.Should().Throw<ArgumentException>().WithMessage("block-port-declaration-invalid:*'json-input'*'A'*");
    }

    [Fact]
    public void Float_in_a_plugins_nested_declaration_is_refused_whatever_its_depth_or_case()
    {
        var factory = new Inline("plugin.block", outputs: [new("A", "A", PortValueType.Of("Collection<Gauge<float>>"), false)]);

        var read = () => BlockShape.FromPlugin(factory, new WorkflowTypeRegistry());

        read.Should().Throw<ArgumentException>().WithMessage("port-type-alias:*'plugin.block'*port 'A'*Float*");
    }

    [Fact]
    public void A_catalogue_of_registered_blocks_refuses_an_invalid_declaration_when_it_is_built()
    {
        var factory = new DeclaredFactory(Rows.Value["expression-on-an-output"]);

        var build = () => new RegisteredWorkflowBlocks([factory]);

        build.Should().Throw<ArgumentException>().WithMessage("block-port-declaration-invalid:*'expression-on-an-output'*'A'*");
    }

    [Fact]
    public void The_catalogue_publishes_a_blocks_type_parameters_and_their_constraints_in_order()
    {
        var factory = new DeclaredFactory(Rows.Value["parameter-with-constraint-and-without"]);

        var described = WorkflowBlockDescription.Describe(factory);

        described.TypeParameters.Select(p => (p.Name, string.Join(",", p.Constraints)))
            .Should().Equal(("T", "Numeric"), ("R", ""));
        described.Inputs.Single().Type.Should().Be("$T");
        described.Outputs.Single().Type.Should().Be("$R");
    }

    [Fact]
    public void The_catalogue_publishes_no_type_parameters_for_a_block_that_declares_none()
    {
        var described = WorkflowBlockDescription.Describe(new DeclaredFactory(Rows.Value["plain-block-without-parameters"]));

        described.TypeParameters.Should().BeEmpty();
    }

    [Fact]
    public void The_catalogue_answers_secretValues_for_a_secret_capable_port_and_only_for_it()
    {
        var factory = new Inline("box", inputs:
        [
            new("Headers", "Headers", PortValueType.OfDeclaration("Map<String,String>"), false) { SecretValues = true },
            new("Other", "Other", PortValueType.OfDeclaration("Map<String,String>"), false),
        ]);

        var described = WorkflowBlockDescription.Describe(factory);

        described.Inputs.Select(p => (p.Name, p.SecretValues)).Should().Equal(("Headers", true), ("Other", false));
    }

    [Fact]
    public void The_catalogue_refuses_an_invalid_declaration_instead_of_describing_it()
    {
        var describe = () => WorkflowBlockDescription.Describe(new DeclaredFactory(Rows.Value["json-input"]));

        describe.Should().Throw<ArgumentException>().WithMessage("block-port-declaration-invalid:*");
    }

    [Fact]
    public void A_shape_carries_a_blocks_type_parameters_without_aliasing_the_callers_lists()
    {
        var constraints = new List<string> { "Numeric" };
        var parameters = new List<WorkflowTypeParameter> { new("T", constraints) };
        var empty = new Dictionary<string, IReadOnlyList<PortShape>>();
        var shape = new BlockShape("box", [], [], [], default, empty, empty, null, TypeParameters: parameters);

        constraints.Add("Comparable");
        parameters.Add(new WorkflowTypeParameter("R"));

        shape.TypeParameters.Should().ContainSingle().Which.Constraints.Should().Equal("Numeric");
    }

    [Theory]
    [InlineData("Expression<Boolean>", true)]
    [InlineData("expression<Int?>", false)]
    [InlineData("Expression<Collection<Int>>", true)]
    [InlineData("Expression<Boolean>?", false)]
    [InlineData("Expression", false)]
    [InlineData("ExpressionX<Boolean>", false)]
    [InlineData("Collection<Expression<Boolean>>", false)]
    [InlineData("String", false)]
    public void Only_a_whole_non_nullable_Expression_term_is_an_expression_type(string text, bool expected)
    {
        PortValueType.OfDeclaration(text).IsExpression.Should().Be(expected);
    }

    [Theory]
    [InlineData("$T", true)]
    [InlineData("Collection<$T?>", true)]
    [InlineData("$T?", true)]
    [InlineData("$", false)]
    [InlineData("$T<Int>", false)]
    public void Only_a_declaration_reads_a_type_parameter(string text, bool parses)
    {
        PortValueType.TryParseDeclaration(text, out _, out _).Should().Be(parses);
        PortValueType.TryParse(text, out _, out _).Should().BeFalse("a stored document or API value can never hold a parameter");
    }

    private static IEnumerable<string> Names(JsonElement row) =>
        row.TryGetProperty("names", out var names) ? names.EnumerateArray().Select(n => n.GetString()!) : [];

    private static BlockPortDefinition[] Ports(JsonElement ports, JsonElement? scopeVariables = null, JsonElement? secretValues = null) =>
        [.. ports.EnumerateObject().Select(p => new BlockPortDefinition(p.Name, p.Name, PortValueType.OfDeclaration(p.Value.GetString()!), false)
        {
            SecretValues = secretValues is { } secret && secret.EnumerateArray().Any(n => n.GetString() == p.Name),
            ScopeVariables = scopeVariables is { } declared && declared.TryGetProperty(p.Name, out var variables)
                ? [.. variables.EnumerateArray().Select(v => new ExpressionScopeVariable(v.GetProperty("name").GetString()!, v.GetProperty("type").GetString()!))]
                : [],
        })];

    internal sealed class DeclaredFactory : IWorkflowBlockFactory
    {
        private readonly JsonElement row;
        private readonly BlockPortDefinition[] inputs;
        private readonly BlockPortDefinition[] outputs;
        private readonly Dictionary<string, (BlockPortDefinition[] Inputs, BlockPortDefinition[] Outputs)> branches;

        public DeclaredFactory(JsonElement row)
        {
            this.row = row;
            JsonElement? secret = row.TryGetProperty("secretValues", out var flagged) ? flagged : null;
            inputs = row.TryGetProperty("inputs", out var i)
                ? Ports(i, row.TryGetProperty("scopeVariables", out var variables) ? variables : null, secret)
                : [];
            outputs = row.TryGetProperty("outputs", out var o) ? Ports(o, secretValues: secret) : [];
            branches = row.TryGetProperty("subCanvases", out var subs)
                ? subs.EnumerateObject().ToDictionary(
                    b => b.Name, b => (Ports(b.Value.GetProperty("inputs"), secretValues: secret), Ports(b.Value.GetProperty("outputs"), secretValues: secret)))
                : [];
        }

        public string BlockTypeId => row.GetProperty("name").GetString()!;
        public string DisplayName => BlockTypeId;
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public bool HasDynamicPorts => row.TryGetProperty("hasDynamicPorts", out var dynamic) && dynamic.GetBoolean();
        public bool SupportsDeclaredOutputs => row.TryGetProperty("supportsDeclaredOutputs", out var supports) && supports.GetBoolean();
        public bool DeclaredOutputsCollect => row.TryGetProperty("declaredOutputsCollect", out var collects) && collects.GetBoolean();
        public IReadOnlyCollection<string> SubCanvasNames => [.. branches.Keys];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string name) => branches[name].Inputs;
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string name) => branches[name].Outputs;

        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => row.TryGetProperty("typeParameters", out var parameters)
            ? [.. parameters.EnumerateArray().Select(p => new WorkflowTypeParameter(
                p.GetProperty("name").GetString()!, [.. p.GetProperty("constraints").EnumerateArray().Select(c => c.GetString()!)]))]
            : [];

        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => [];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class Inline(
        string id, BlockPortDefinition[]? inputs = null, BlockPortDefinition[]? outputs = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
