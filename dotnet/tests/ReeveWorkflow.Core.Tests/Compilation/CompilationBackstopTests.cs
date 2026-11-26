using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationBackstopTests
{
    private static WorkflowDefinition Nested()
    {
        var box = Node("box", "box");
        box.SubCanvases = new() { ["Body"] = Graph([StartNode(), Node("a", "a")], [Edge("b", "start", "a")]) };
        return Graph([StartNode(), box], [Edge("e", "start", "box")]);
    }

    private static IBlockShapeSource Shapes() => Catalogue(Shape("a"), Shape("box", subCanvasNames: ["Body"]));

    private static StoredWorkflowDocument Document(WorkflowDefinition definition) =>
        StoredWorkflowDocument.ReadModel(definition, out _)!;

    public static TheoryData<string> PhaseNames() => new(WorkflowCompiler.Phases.Select(p => p.Name));

    [Fact]
    public void The_graph_every_fault_is_injected_into_compiles_without_one()
    {
        var result = WorkflowCompiler.Compile(Document(Nested()), Shapes(), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
        result.Fault.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(PhaseNames))]
    public void A_fault_in_each_phase_is_one_compilation_fault_naming_the_phase_and_canvas_and_nothing_runs_after_it(string name)
    {
        var injected = new InvalidOperationException("injected");
        var ran = new List<CanvasPath>();
        var phases = WorkflowCompiler.Phases
            .Select(p => p.Name != name ? p : p with
            {
                Run = (frame, context) =>
                {
                    if (!frame.Path.IsRoot)
                        throw injected;
                    p.Run(frame, context);
                },
            })
            .Append(new CompilerPhase("after", (frame, _) => ran.Add(frame.Path)))
            .ToList();

        var result = WorkflowCompiler.Compile(Document(Nested()), Shapes(), CanvasSignature.Empty, phases: phases);

        result.Succeeded.Should().BeFalse();
        var fault = result.Diagnostics.Should().ContainSingle().Which;
        fault.RuleId.Should().Be(CompilationRules.CompilationFault);
        fault.Message.Should().Be($"The compiler could not finish its '{name}' phase: InvalidOperationException: injected");
        fault.NodeId.Should().BeNull();
        fault.CanvasPath.Should().Equal(new CanvasPathSegment("box", "Body"));
        result.Fault.Should().BeSameAs(injected);
        ran.Should().BeEmpty("phases run one at a time over every canvas, so the later one never starts");
    }

    [Fact]
    public void A_fault_on_the_first_canvas_ends_the_compile_and_nothing_after_it_runs()
    {
        var injected = new InvalidOperationException("injected");
        var ran = new List<(string Phase, CanvasPath Path)>();
        var phases = WorkflowCompiler.Phases
            .Select(p => p with
            {
                Run = (frame, context) =>
                {
                    if (frame.Path.IsRoot && p.Name == "check-nodes")
                        throw injected;
                    ran.Add((p.Name, frame.Path));
                    p.Run(frame, context);
                },
            })
            .ToList();

        var result = WorkflowCompiler.Compile(Document(Nested()), Shapes(), CanvasSignature.Empty, phases: phases);

        result.Diagnostics.Should().ContainSingle().Which.CanvasPath.Should().BeEmpty();
        result.Fault.Should().BeSameAs(injected);
        ran.Should().NotContain(r => r.Phase == "check-nodes" || r.Phase == "bind-types");
    }

    [Fact]
    public void A_fault_while_lowering_is_a_compilation_fault_naming_the_canvas_being_lowered()
    {
        var injected = new InvalidOperationException("injected");
        var result = WorkflowCompiler.Compile(
            Document(Nested()), Shapes(), CanvasSignature.Empty,
            lower: (frame, _) => frame.Path.IsRoot ? new CompiledCanvas(frame.Path, CanvasSignature.Empty, [], []) : throw injected);

        var fault = result.Diagnostics.Should().ContainSingle().Which;
        fault.RuleId.Should().Be(CompilationRules.CompilationFault);
        fault.Message.Should().StartWith($"The compiler could not finish its '{WorkflowCompiler.LowerPhase}' phase:");
        fault.CanvasPath.Should().Equal(new CanvasPathSegment("box", "Body"));
        result.Fault.Should().BeSameAs(injected);
        result.BoundShapes.Keys.Should().Contain("box", "what bind reached is still reported on a refusal");
    }

    [Fact]
    public void What_the_phases_filed_before_the_fault_stands()
    {
        var graph = Graph([StartNode(), Node("u", "unknown")], [Edge("e", "start", "u")]);
        var phases = WorkflowCompiler.Phases
            .Select(p => p.Name != "check-nodes" ? p : p with { Run = (_, _) => throw new InvalidOperationException("injected") })
            .ToList();

        var result = WorkflowCompiler.Compile(Document(graph), Shapes(), CanvasSignature.Empty, phases: phases);

        result.Diagnostics.Select(d => d.RuleId).Should().Equal(CompilationRules.BlockTypeUnknown, CompilationRules.CompilationFault);
    }

    private sealed class ThrowingShapes(IBlockShapeSource inner, string blockTypeId, int onCall, Exception error) : IBlockShapeSource
    {
        private int calls;

        public BlockShape? Find(string id) =>
            id == blockTypeId && ++calls == onCall ? throw error : inner.Find(id);
    }

    public static TheoryData<Exception> HostFaults() =>
        new(new InvalidOperationException("host down"), new ArgumentException("host down"), new HttpRequestException("host down"));

    [Theory]
    [MemberData(nameof(HostFaults))]
    public void A_catalogue_that_throws_in_bind_is_a_compilation_fault_naming_the_node(Exception error)
    {
        var graph = Graph([StartNode(), Node("n", "a")], [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, new ThrowingShapes(Shapes(), "a", 1, error), CanvasSignature.Empty);

        var fault = result.Diagnostics.Should().ContainSingle().Which;
        (fault.RuleId, fault.NodeId).Should().Be((CompilationRules.CompilationFault, "n"));
        fault.Message.Should().Be($"The compiler could not finish its 'bind' phase at node 'n': {error.GetType().Name}: host down");
        result.Fault.Should().BeSameAs(error);
    }

    [Fact]
    public void A_fault_at_a_node_inside_a_branch_names_that_branchs_canvas_not_the_one_being_checked()
    {
        var error = new InvalidOperationException("catalogue down");
        var box = Node("box", "box");
        box.SubCanvases = new()
        {
            ["Body"] = Graph(
                [StartNode("in"), Node("s", "s"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
                [Edge("b1", "in", "s"), Edge("b2", "s", "out", ("v", "Result"))]),
        };
        var graph = Graph([StartNode(), box, Node("after", "after")], [Edge("e1", "start", "box"), Edge("e2", "box", "after", ("Results", "x"))]);
        var boxShape = new BlockShape(
            "box", [], [new PortShape("Results", PortValueType.OfDeclaration("Collection<$R>"), false)],
            [], default, new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] },
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [new PortShape("Result", PortValueType.OfDeclaration("$R"), false)] },
            null, null, ["Body"], [new WorkflowTypeParameter("R")]);
        var shapes = Catalogue(boxShape, Shape("s", outputs: [("v", PortValueType.Int)]), Shape("after", inputs: [("x", PortValueType.CollectionOf(PortValueType.Int), false)]));
        WorkflowCompiler.Compile(graph, shapes, CanvasSignature.Empty).Succeeded.Should().BeTrue();

        var result = WorkflowCompiler.Compile(graph, new ThrowingShapes(shapes, "s", 1, error), CanvasSignature.Empty);

        var fault = result.Diagnostics.Should().ContainSingle().Which;
        (fault.RuleId, fault.NodeId).Should().Be((CompilationRules.CompilationFault, "s"));
        fault.CanvasPath.Should().Equal(new CanvasPathSegment("box", "Body"));
        result.Fault.Should().BeSameAs(error);
    }

    internal sealed class HostCheck(Func<Exception?> fault) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "host";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            fault() is { } error ? throw error : new(NamespacePathCheck.Yes, "String");
    }

    internal static WorkflowDefinition AskingTheHost() =>
        Graph([StartNode(), Node("x", "a", new { v = "expr:${host.q}" })], [Edge("e", "start", "x")]);

    [Theory]
    [MemberData(nameof(HostFaults))]
    public void A_namespace_check_that_throws_is_a_compilation_fault_naming_the_node_whose_expression_asked(Exception error)
    {
        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new HostCheck(() => error))] };

        var result = WorkflowCompiler.Compile(AskingTheHost(), Shapes(), CanvasSignature.Empty, options);

        var fault = result.Diagnostics.Should().ContainSingle().Which;
        (fault.RuleId, fault.NodeId).Should().Be((CompilationRules.CompilationFault, "x"));
        fault.Message.Should().StartWith("The compiler could not finish its 'bind-types' phase at node 'x':");
        result.Fault.Should().BeSameAs(error);
    }

    public static TheoryData<Exception> NotCaught() => new(new OperationCanceledException(), new TaskCanceledException(), new OutOfMemoryException(), new InsufficientMemoryException());

    [Theory]
    [MemberData(nameof(NotCaught))]
    public void A_cancellation_or_memory_exhaustion_is_not_caught_wherever_host_code_raises_it(Exception error)
    {
        var graph = Graph([StartNode(), Node("n", "a")], [Edge("e", "start", "n")]);
        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new HostCheck(() => error))] };

        FluentActions.Invoking(() => WorkflowCompiler.Compile(graph, new ThrowingShapes(Shapes(), "a", 1, error), CanvasSignature.Empty))
            .Should().Throw<Exception>().Which.Should().BeSameAs(error);
        FluentActions.Invoking(() => WorkflowCompiler.Compile(AskingTheHost(), Shapes(), CanvasSignature.Empty, options))
            .Should().Throw<Exception>().Which.Should().BeSameAs(error);
    }

    [Theory]
    [MemberData(nameof(NotCaught))]
    public void A_cancellation_or_memory_exhaustion_is_not_caught_in_a_phase_or_while_lowering(Exception error)
    {
        var phases = WorkflowCompiler.Phases.Select(p => p with { Run = (_, _) => throw error }).ToList();

        FluentActions.Invoking(() => WorkflowCompiler.Compile(Document(Nested()), Shapes(), CanvasSignature.Empty, phases: phases))
            .Should().Throw<Exception>().Which.Should().BeSameAs(error);
        FluentActions.Invoking(() => WorkflowCompiler.Compile(Document(Nested()), Shapes(), CanvasSignature.Empty, lower: (_, _) => throw error))
            .Should().Throw<Exception>().Which.Should().BeSameAs(error);
    }
}

public class CompilationFaultServiceTests
{
    internal sealed class ErrorLog : ILogger<WorkflowCompilationService>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private static (WorkflowCompilationService Service, CompilationServiceTests.RecordingStore Store, ErrorLog Log) Build(Func<Exception?> fault)
    {
        var store = new CompilationServiceTests.RecordingStore();
        var log = new ErrorLog();
        var service = CompilationScopes.Service(
            new CompilationServiceTests.MovableCatalogue().Add(Shape("a")), new CompilationServiceTests.FakeWorkflowStore(), store,
            new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new CompilationBackstopTests.HostCheck(fault))] }, logger: log);
        return (service, store, log);
    }

    [Fact]
    public async Task A_faulted_compile_is_thrown_logged_once_and_not_recorded_so_the_next_call_compiles_again()
    {
        var error = new InvalidOperationException("check down");
        Exception? next = error;
        var (service, store, log) = Build(() => next);
        var version = CompilationServiceTests.Version(CompilationBackstopTests.AskingTheHost());

        var thrown = (await FluentActions.Awaiting(() => service.EnsureAsync(version, null, CancellationToken.None))
            .Should().ThrowAsync<CompilationFaultException>()).Which;

        thrown.InnerException.Should().BeSameAs(error);
        (thrown.Diagnostic.RuleId, thrown.Diagnostic.NodeId).Should().Be((CompilationRules.CompilationFault, "x"));
        thrown.Message.Should().Be(thrown.Diagnostic.Message);
        store.Saves.Should().Be(0);
        log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Error, error));

        next = null;
        var verdict = await service.EnsureAsync(version, null, CancellationToken.None);

        verdict.IsValid.Should().BeTrue("the fault was not memoised: this call compiled again");
        store.Saves.Should().Be(1);
        store.Finds.Should().Be(2, "the faulted call asked the store before compiling, as every miss does");
    }

    [Fact]
    public async Task The_exception_carries_the_compilation_fault_and_not_a_refusal_filed_before_it()
    {
        var (service, _, _) = Build(() => new InvalidOperationException("check down"));
        var graph = CompilationBackstopTests.AskingTheHost();
        graph.Nodes = [.. graph.Nodes, Node("u", "unknown")];
        graph.Edges = [.. graph.Edges!, Edge("e2", "start", "u")];

        var thrown = (await FluentActions.Awaiting(() => service.EnsureAsync(CompilationServiceTests.Version(graph), null, CancellationToken.None))
            .Should().ThrowAsync<CompilationFaultException>()).Which;

        (thrown.Diagnostic.RuleId, thrown.Diagnostic.NodeId).Should().Be((CompilationRules.CompilationFault, "x"));
    }

    [Fact]
    public async Task A_sweep_reports_a_faulted_compile_as_one_it_could_not_answer()
    {
        var (service, store, _) = Build(() => new InvalidOperationException("check down"));
        var version = CompilationServiceTests.Version(CompilationBackstopTests.AskingTheHost());

        var report = await new WorkflowCompilationSweeper(service, store)
            .SweepAsync(Listing(new CurrentWorkflowVersion(version.WorkflowId, version)), CancellationToken.None);

        report.Failed.Should().ContainSingle().Which.Error.Should().BeOfType<CompilationFaultException>();
        report.Current.Should().BeEmpty();
        report.Invalid.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<CurrentWorkflowVersion> Listing(params CurrentWorkflowVersion[] versions)
    {
        foreach (var version in versions)
        {
            await Task.Yield();
            yield return version;
        }
    }
}
