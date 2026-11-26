using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class FilterBlockTests
{
    private const string Graph = """
        {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{}},
        {"id":"f","blockTypeId":"Filter","position":{"x":240,"y":0},"configuration":{"Predicate":"PREDICATE"}},
        {"id":"after","blockTypeId":"Math","position":{"x":360,"y":0},"configuration":{"A":1,"B":"AFTER"}}],
        "edges":[{"id":"e0","sourceNodeId":"start","targetNodeId":"src","mappings":{}},
        {"id":"e1","sourceNodeId":"src","targetNodeId":"f","mappings":{"Collection":{"type":"direct","sourceOutputName":"SOURCE"}}},
        {"id":"e2","sourceNodeId":"f","targetNodeId":"after","mappings":{}}]}
        """;

    private static WorkflowDefinition Filter(string predicate, string source = "Items", object? after = null) =>
        CompiledGraph.Read(Graph
            .Replace("\"PREDICATE\"", System.Text.Json.JsonSerializer.Serialize(predicate))
            .Replace("\"AFTER\"", System.Text.Json.JsonSerializer.Serialize(after ?? 2))
            .Replace("SOURCE", source));

    private static readonly RegisteredWorkflowBlocks Blocks = CompiledGraph.Blocks(new Source());

    [Fact]
    public async Task A_Collection_of_Int_keeps_the_items_the_predicate_answers_true_for_as_a_list_of_long()
    {
        var outputs = await CompiledGraph.RunAsync(Filter("expr:${item} > 2"), Blocks);

        outputs["f"]["Filtered"].Should().BeOfType<List<long>>().Which.Should().Equal(3L, 5L, 4L);
    }

    [Fact]
    public async Task Filter_predicate_is_compared_for_each_item()
    {
        var outputs = await CompiledGraph.RunAsync(Filter("expr:${item} > 2"), CompiledGraph.Blocks(new Source([1L, 3L, 2L])));
        outputs["f"]["Filtered"].Should().BeOfType<List<long>>().Which.Should().Equal(3L);
    }

    [Fact]
    public async Task A_null_item_of_a_nullable_T_is_evaluated_like_any_other()
    {
        var outputs = await CompiledGraph.RunAsync(Filter("expr:${item} == null || ${item} == 5", "Maybe"), Blocks);

        outputs["f"]["Filtered"].Should().BeOfType<List<long?>>().Which.Should().Equal(null, 5L);
    }

    [Fact]
    public async Task A_predicate_that_answers_false_for_every_item_gives_an_empty_collection_of_the_term()
    {
        var outputs = await CompiledGraph.RunAsync(Filter("expr:false"), Blocks);

        outputs["f"]["Filtered"].Should().BeOfType<List<long>>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task A_predicate_that_fails_at_run_time_fails_the_node_with_expression_runtime_error()
    {
        var definition = Filter("expr:${item} % ${previous.src.Zero} == 0");
        definition.Nodes.Single(n => n.Id == "f").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await CompiledGraph.RunAsync(definition, Blocks);

        var error = (Dictionary<string, object?>)outputs["f"]["_error"]!;
        error["code"].Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
        error["category"].Should().Be("division-by-zero");
        outputs["f"].Should().NotContainKey("Filtered");
    }

    [Fact]
    public void A_predicate_whose_result_is_not_Boolean_is_refused_at_compile()
    {
        CompiledGraph.Refusals(Filter("expr:${item} + 1"), Blocks).Should().Equal("expression-result-type-mismatch@f.Predicate");
    }

    [Fact]
    public void Item_read_outside_the_predicate_is_out_of_scope()
    {
        CompiledGraph.Compile(Filter("expr:${item} > 2", after: "expr:${item}"), Blocks).Diagnostics
            .Select(d => (d.RuleId, d.NodeId)).Should().Equal(("expression-out-of-scope", "after"));
    }

    [Fact]
    public void Filter_declares_its_ports_with_one_type_parameter_and_the_item_scope_variable()
    {
        var factory = new FilterBlockFactory();

        factory.TypeParameters.Select(p => p.Name).Should().Equal("T");
        factory.Inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(
            ("Collection", "Collection<$T>", true), ("Predicate", "Expression<Boolean>", true));
        factory.Inputs.Single(p => p.Name == "Predicate").ScopeVariables.Should().Equal(new ExpressionScopeVariable("item", "$T"));
        factory.Outputs.Single(p => p.Name == "Filtered").Type.Name.Should().Be("Collection<$T>");
        factory.ConfigurationSchema.Select(f => (f.Name, f.Type)).Should().Equal(("Predicate", ConfigFieldType.String));
        BlockShape.From(factory).Inputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_context_built_outside_a_compiled_run_is_refused()
    {
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance, Services = new NoServices(),
        };

        var act = () => new FilterBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<long> { 1 } }, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not compiled*");
    }

    [Fact]
    public async Task A_context_with_types_but_not_those_of_a_bound_T_is_refused_rather_than_dropping_Filtered()
    {
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance, Services = new NoServices(),
            Types = new Dictionary<string, ClosedPortType>
            {
                ["$T"] = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("Int", PortTermGrammar.Document))!,
            },
        };

        var act = () => new FilterBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<long> { 1 } }, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'Filtered'*");
    }

    private sealed class Source(long[]? items = null) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public string BlockTypeId => "src";
        public string DisplayName => "src";
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
        [
            new("Items", "Items", PortValueType.OfDeclaration("Collection<Int>"), false),
            new("Maybe", "Maybe", PortValueType.OfDeclaration("Collection<Int?>"), false),
            new("Zero", "Zero", PortValueType.Int, false),
        ];

        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["Items"] = context.Types["Items"].CreateList(items is null ? new object?[] { 1L, 3L, 2L, 5L, 4L } : items.Cast<object?>()),
                ["Maybe"] = context.Types["Maybe"].CreateList([1L, null, 5L]),
                ["Zero"] = 0L,
            });
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
