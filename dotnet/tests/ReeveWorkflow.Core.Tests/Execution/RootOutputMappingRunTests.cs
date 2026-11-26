using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RootOutputMappingRunTests
{
    private const string Produce = "t23.produce";

    public static TheoryData<string, string?, bool> Declarations() => new()
    {
        { "none declared (null)", null, false },
        { "none declared ([])", "[]", false },
        { "only another declared", """[{"name":"Other","type":"String","isRequired":false}]""", false },
        { "declared in another case", """[{"name":"Final","type":"String","isRequired":false}]""", false },
        { "declared", """[{"name":"final","type":"String","isRequired":false}]""", true },
    };

    private static WorkflowDefinition Mapped() => Graph(
        [StartNode(), Node("p", Produce), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
        [Edge("e1", "start", "p"), Edge("e2", "p", "wo", ("Value", "final"))]);

    [Theory]
    [MemberData(nameof(Declarations))]
    public async Task The_walk_writes_the_wired_name_and_the_compiler_refuses_it_unless_declared(
        string label, string? outputParametersJson, bool declared)
    {
        var walked = await Walk(Mapped());
        WorkflowExecutionHelpers.BuildResultOutputs(walked, TestPrograms.Walk(Mapped(), Blocks()))
            .Should().Equal(new Dictionary<string, object?> { ["final"] = "A" }, label);

        CanvasSignature.TryRead(null, outputParametersJson, out var signature, out _).Should().BeTrue();
        var diagnostics = WorkflowCompiler.Compile(Mapped(), new CatalogueBlockShapeSource(Blocks()), signature!).Diagnostics;

        if (declared)
        {
            diagnostics.Should().BeEmpty(label);
            return;
        }

        diagnostics.Should().ContainSingle(label).Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "wo" && d.PortName == "final"
            && d.CanvasPath.Count == 0 && d.Message.Contains("has no input 'final'"));
    }

    [Theory]
    [MemberData(nameof(Declarations))]
    public async Task Admission_refuses_the_start_unless_the_output_is_declared(
        string label, string? outputParametersJson, bool declared)
    {
        var world = new DurableSleepWorld();
        world.Blocks.Extra = id => id == Produce ? new ProduceFactory() : null;
        var t0 = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var workflow = new Workflow(workflowId, "wf", null, owner, t0);
        workflow.SetCurrentVersion(new WorkflowVersion(
            Guid.NewGuid(), workflowId, 1, Stored(Mapped()), null, outputParametersJson, owner, t0, isPublished: true), t0);
        world.Store.Add(workflow);
        using var engine = world.Engine();

        var run = async () => await engine.ExecuteAsync(workflowId, null, null);

        if (declared)
        {
            (await run.Should().NotThrowAsync(label)).Which.Should().Equal(new Dictionary<string, object?> { ["final"] = "A" });
            return;
        }

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>(label)).Which.Diagnostics
            .Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.WirePortUnknown && d.PortName == "final");
    }

    private static RegisteredWorkflowBlocks Blocks() => new([new ProduceFactory()]);

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> Walk(WorkflowDefinition definition)
    {
        var executor = new WorkflowGraphExecutor(
            Blocks(), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance);
        return await executor.ExecuteGraphAsync(
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(),
            [], [], WorkflowTraceMode.None, debugMode: false, iterationIndex: null, callStackJson: null,
            depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
            triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
            stateExecutionId: null, compiled: TestPrograms.Walk(definition, Blocks()));
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ProduceFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => Produce;
        public string DisplayName => Produce;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Value", "Value", PortValueType.String, false)];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new Plugin();

        private sealed class Plugin : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Value"] = "A" });
        }
    }
}
