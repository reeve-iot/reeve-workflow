using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ExpressionBlockTests
{
    private static WorkflowBlockContext MakeContext() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>()
    };

    [Fact]
    public async Task Surfaces_the_evaluated_value_on_Result()
    {
        var block = new ExpressionBlock();

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Expression"] = true },
            MakeContext(), CancellationToken.None);

        result["Result"].Should().Be(true);
    }

    [Fact]
    public async Task Passes_a_non_boolean_result_through_unchanged()
    {
        var block = new ExpressionBlock();

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Expression"] = 21.5 },
            MakeContext(), CancellationToken.None);

        result["Result"].Should().Be(21.5);
    }

    [Fact]
    public async Task Fails_cleanly_when_Expression_is_absent()
    {
        var block = new ExpressionBlock();

        var act = () => block.ExecuteAsync(
            new Dictionary<string, object?>(), MakeContext(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Expression*");
    }

    [Theory]
    [InlineData("Int", null)]
    [InlineData("Number", null)]
    [InlineData("Boolean", CompilationRules.PortTypeMismatch)]
    public void Result_carries_the_term_of_the_expression_that_feeds_it(string sink, string? rule)
    {
        var diagnostics = Compile("expr:${previous.a.X} + 1", sink).Diagnostics;

        if (rule is null)
            diagnostics.Should().BeEmpty(string.Join("; ", diagnostics.Select(d => d.Message)));
        else
            diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d => d.RuleId == rule && d.NodeId == "sink");
    }

    [Fact]
    public void A_now_hour_result_wired_into_a_Number_input_compiles()
    {
        Compile("expr:${now.hour}", "Number").Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_expression_whose_result_is_null_leaves_T_unbound()
    {
        Compile("expr:null", "Int").Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.TypeParameterUnbound && d.NodeId == "x");
    }

    [Fact]
    public async Task A_run_hands_an_Int_result_on_as_a_long()
    {
        var seen = new Dictionary<string, object?>();
        var compiled = Compile("expr:${previous.a.X} + 1", "Int", seen);
        compiled.Diagnostics.Should().BeEmpty();
        var executor = new WorkflowGraphExecutor(
            compiled.Blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());

        await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = new Dictionary<string, object?>() },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Substitute.For<IServiceProvider>(), [], [], WorkflowTraceMode.None,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: new Dictionary<string, object?>(), triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(compiled.Program!));

        seen.Should().ContainKey("sink").WhoseValue.Should().BeOfType<long>().Which.Should().Be(6L);
    }

    private sealed record Compiled(
        IReadOnlyList<CompilationDiagnostic> Diagnostics, WorkflowProgram? Program, WorkflowDefinition Definition, IWorkflowBlocks Blocks);

    private static Compiled Compile(string expression, string sinkTerm, Dictionary<string, object?>? seen = null)
    {
        var json = """
            {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
            {"id":"a","blockTypeId":"source","position":{"x":100,"y":0},"configuration":{}},
            {"id":"x","blockTypeId":"Expression","position":{"x":200,"y":0},"configuration":{"Expression":EXPRESSION}},
            {"id":"sink","blockTypeId":"sink","position":{"x":300,"y":0},"configuration":{}}],
            "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"a","mappings":{}},{"id":"e1","sourceNodeId":"a","targetNodeId":"x","mappings":{}},
            {"id":"e2","sourceNodeId":"x","targetNodeId":"sink","mappings":{"In":{"type":"direct","sourceOutputName":"Result"}}}]}
            """.Replace("EXPRESSION", System.Text.Json.JsonSerializer.Serialize(expression), StringComparison.Ordinal);
        var definition = StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());
        var blocks = new RegisteredWorkflowBlocks(
        [
            new ExpressionBlockFactory(),
            new TestFactory("source", [], [new("X", "X", PortValueType.Int, false)], _ => new Dictionary<string, object?> { ["X"] = 5L }),
            new TestFactory("sink", [new("In", "In", PortValueType.Of(sinkTerm), true)], [], inputs =>
            {
                if (seen is not null)
                    seen["sink"] = inputs.GetValueOrDefault("In");
                return new Dictionary<string, object?>();
            }),
        ]);
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        return new Compiled(result.Diagnostics, result.Program, definition, blocks);
    }

    private sealed class TestFactory(
        string id, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> run) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> values, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult(run(values));
    }
}
