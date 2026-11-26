using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CatalogueBlockShapeSourceTests
{
    [Fact]
    public void Matching_is_case_insensitive_like_the_catalogue_it_reads()
    {
        var blocks = new RegisteredWorkflowBlocks([new FakeFactory("Bt.Sensor")]);
        var source = new CatalogueBlockShapeSource(blocks);

        source.Find("bt.sensor").Should().NotBeNull();
    }

    [Theory]
    [InlineData("WorkflowInput")]
    [InlineData("workflowinput")]
    [InlineData("WorkflowOutput")]
    public void A_host_registry_holding_a_boundary_block_type_id_is_refused_as_reserved(string id)
    {
        var act = () => new CatalogueBlockShapeSource(new MinimalBlocks(new FakeFactory("plain"), new FakeFactory(id)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{id}' is reserved*");
    }

    public static TheoryData<string> RefusedDeclarations() =>
    [
        .. BlockDeclarationTests.Rows.Value
            .Where(r => r.Value.GetProperty("expect").ValueKind == JsonValueKind.String
                && r.Value.GetProperty("expect").GetString() == "block-port-declaration-invalid"
                && !r.Value.TryGetProperty("expectDotnet", out _))
            .Select(r => r.Key),
    ];

    [Theory]
    [MemberData(nameof(RefusedDeclarations))]
    public void A_host_catalogue_holding_a_block_that_breaks_a_declaration_rule_fails_startup_naming_the_block_and_port(string name)
    {
        var row = BlockDeclarationTests.Rows.Value[name];
        var blocks = new MinimalBlocks(new BlockDeclarationTests.DeclaredFactory(row));

        var start = () => CompilationScopes.Service(blocks, new CompilationServiceTests.FakeWorkflowStore());

        var message = start.Should().Throw<ArgumentException>(name).Which.Message;
        message.Should().StartWith("block-port-declaration-invalid:").And.Contain($"'{name}'");
        foreach (var named in row.TryGetProperty("names", out var listed) ? listed.EnumerateArray().Select(n => n.GetString()!) : [])
            message.Should().Contain(named, $"{name}: the refusal names it");
    }

    [Fact]
    public void A_host_catalogue_holding_a_dotted_port_name_fails_startup_with_the_dotted_name_message()
    {
        var blocks = new MinimalBlocks(new PortsFactory("host.dotted", [("result.value", "String")], null));

        var start = () => CompilationScopes.Service(blocks, new CompilationServiceTests.FakeWorkflowStore());

        start.Should().Throw<ArgumentException>()
            .WithMessage("Block type 'host.dotted' declares name 'result.value' containing a dot (rule dotted-name).*");
    }

    [Fact]
    public void A_host_catalogue_holding_a_malformed_enum_field_fails_startup_under_its_rule()
    {
        var blocks = new MinimalBlocks(new EnumFieldFactory());

        var start = () => CompilationScopes.Service(blocks, new CompilationServiceTests.FakeWorkflowStore());

        start.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("'host.setter'").And.Contain("'Kind'").And.Contain($"(rule {EnumFieldRules.NoValues})");
    }

    [Fact]
    public async Task A_declaration_a_catalogue_adds_later_throws_out_of_the_compile_that_meets_it_and_is_never_a_diagnostic()
    {
        var world = new CompilationServiceTests.ServiceWorld();
        world.Blocks.Move("gen-2", Shape("bad", inputs: [("A", PortValueType.Json, false)]));
        var graph = Graph([StartNode(), Node("n1", "bad")], [Edge("e1", "start", "n1")]);

        var check = async () => await world.Service.CheckAsync(Stored(graph), null, null, CancellationToken.None);

        await check.Should().ThrowAsync<ArgumentException>().WithMessage("block-port-declaration-invalid:*'bad'*'A'*");
    }

    [Fact]
    public void A_compile_admits_a_host_registered_block_whose_declaration_is_sound()
    {
        var source = new CatalogueBlockShapeSource(new MinimalBlocks(new PortsFactory("host.waiter", [("A", "Expression<Boolean>")], null)));
        var graph = Graph([StartNode(), Waiter()], [Edge("e1", "start", "n1")]);

        WorkflowCompiler.Compile(graph, source, CanvasSignature.Empty).Diagnostics.Should().BeEmpty();
    }

    private static WorkflowNodeDefinition Waiter() =>
        Node("n1", "host.waiter", new Dictionary<string, object?> { ["A"] = "expr:true" });

    private sealed class EnumFieldFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => "host.setter";
        public string DisplayName => "Setter";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
            [new("Kind", "Kind", ConfigFieldType.Enum, false) { AllowedValues = [] }];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class PortsFactory(string blockTypeId, (string Name, string Type)[] inputs, string? parameter) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => "Declared";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [.. inputs.Select(p => new BlockPortDefinition(p.Name, p.Name, PortValueType.OfDeclaration(p.Type), false))];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => parameter is null ? [] : [new WorkflowTypeParameter(parameter, [])];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    [Fact]
    public void A_factorys_flipping_block_type_id_no_longer_collapses_two_reached_shapes_into_one_hash()
    {
        (string Hash, string Diags) Run(string requiredInput)
        {
            var flip = new FlippingIdFactory("alt", "plain", requiredInput);
            flip.Arm();
            var blocks = new MinimalBlocks(flip, new FakeFactory("plain"));
            var source = new CatalogueBlockShapeSource(blocks);
            var graph = Graph([StartNode(), Node("n1", "alt"), Node("n2", "plain")],
                [Edge("e1", "start", "n1"), Edge("e2", "n1", "n2")]);
            var used = WorkflowCompiler.ShapesUsedBy(graph, source);
            var result = WorkflowCompiler.Compile(graph, source, CanvasSignature.Empty);
            var diags = string.Join(" | ", result.Diagnostics.Select(d => $"{d.RuleId}:{d.PortName}"));
            return (DependencyFingerprint.Compute(used.Values, new CompilationOptions(), NodePortEnvironment.Empty), diags);
        }

        var x = Run("X");
        var y = Run("Y");

        x.Diags.Should().NotBe(y.Diags, "the two catalogues bind different required-input names");
        x.Hash.Should().NotBe(y.Hash, "the two catalogues compile to different diagnostics");
    }

    private sealed class FlippingIdFactory(string first, string then, string requiredInput) : IWorkflowBlockFactory
    {
        private int reads = -1;
        public void Arm() => reads = 0;
        public string BlockTypeId => reads < 0 ? first : reads++ == 0 ? first : then;
        public string DisplayName => "Flip";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition(requiredInput, requiredInput, PortValueType.Number, true)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class FakeFactory(string blockTypeId) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => "Fake";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class MinimalBlocks(params IWorkflowBlockFactory[] all) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => all.FirstOrDefault(f => f.BlockTypeId == blockTypeId);
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => all;
    }
}
