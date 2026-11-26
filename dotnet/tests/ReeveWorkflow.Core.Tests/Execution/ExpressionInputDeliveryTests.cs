using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ExpressionInputDeliveryTests
{
    private const string Text = "${previous.src.v} > 3";

    private const string Definition = """
        {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{}},
        {"id":"w","blockTypeId":"waiter","position":{"x":240,"y":0},"configuration":{"Predicate":"expr:${previous.src.v} > 3","Label":"expr:\"a\" + \"b\""}},
        {"id":"x","blockTypeId":"waiter","position":{"x":360,"y":0},"configuration":{}}],
        "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"src","mappings":{}},{"id":"e1","sourceNodeId":"src","targetNodeId":"w","mappings":{}},
        {"id":"e2","sourceNodeId":"w","targetNodeId":"x","mappings":{"Predicate":{"type":"literal","literalValue":"expr:${previous.src.v} > 3"}}}]}
        """;

    [Fact]
    public async Task An_Expression_input_receives_the_text_and_a_plain_input_of_the_same_node_the_evaluated_value()
    {
        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await RunAsync(seen);

        seen["w"]["Predicate"].Should().Be("expr:" + Text, "the block evaluates the Expression<Boolean> input itself, later");
        seen["w"]["Label"].Should().Be("ab", "an input of any other type is evaluated before the block runs");
    }

    [Fact]
    public async Task An_Expression_input_fed_by_a_literal_mapping_receives_the_text_too()
    {
        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await RunAsync(seen);

        seen["x"]["Predicate"].Should().Be("expr:" + Text);
    }

    [Fact]
    public async Task A_node_reading_the_same_helper_twice_uses_one_read_snapshot()
    {
        var resolver = new CountingHelperResolver();
        var definition = StoredWorkflowDocument.Read(Definition.Replace(
            "\"Predicate\":\"expr:${previous.src.v} > 3\"", "\"Predicate\":\"expr:${helper.k} + ${helper.k} > 0\""), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());

        await RunAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>>(), NullWorkflowNodeObserver.Instance, definition, resolver);

        resolver.ReadBatches.Should().ContainSingle().Which.Select(path => string.Join('.', path.Segments)).Should().ContainSingle().Which.Should().Be("k");
    }

    [Fact]
    public async Task Reading_compiled_helper_paths_does_not_change_compiled_node_record_equality()
    {
        var resolver = new CountingHelperResolver();
        var definition = StoredWorkflowDocument.Read(Definition.Replace(
            "\"Predicate\":\"expr:${previous.src.v} > 3\"", "\"Predicate\":\"expr:${helper.k} > 0\""), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var compiled = await WorkflowCompiler.CompileAsync(definition, new CatalogueBlockShapeSource(Catalogue(new())),
            CanvasSignature.Empty, new CompilationOptions { HelperResolver = resolver });
        var node = compiled.Program!.Canvases.SelectMany(canvas => canvas.Nodes).Single(candidate => candidate.NodeId == "w");
        var equalNode = node with { };
        node.Should().Be(equalNode);

        _ = node.HelperPaths;

        node.Should().Be(equalNode);
    }

    [Fact]
    public async Task A_node_without_helper_paths_does_not_check_or_read_helpers()
    {
        var resolver = new CountingHelperResolver();
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());

        await RunAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>>(), NullWorkflowNodeObserver.Instance, definition, resolver);

        resolver.CheckCalls.Should().Be(0);
        resolver.ReadBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Public_compile_asks_helpers_once_and_check_failure_is_a_compilation_fault()
    {
        var resolver = new ThrowingCheckResolver();
        var definition = StoredWorkflowDocument.Read(Definition.Replace(
            "\"Predicate\":\"expr:${previous.src.v} > 3\"", "\"Predicate\":\"expr:${helper.k} > 0\""), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var options = new CompilationOptions { HelperResolver = resolver };

        var act = () => WorkflowCompiler.CompileAsync(
            definition, new CatalogueBlockShapeSource(Catalogue(new())), CanvasSignature.Empty, options);

        await act.Should().ThrowAsync<CompilationFaultException>().WithMessage("*check failed*");
        resolver.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Public_compile_does_not_check_when_the_document_reads_no_helper_path()
    {
        var resolver = new ThrowingCheckResolver();
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());

        var result = await WorkflowCompiler.CompileAsync(definition,
            new CatalogueBlockShapeSource(Catalogue(new())), CanvasSignature.Empty,
            new CompilationOptions { HelperResolver = resolver });

        result.Diagnostics.Should().BeEmpty();
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_throwing_node_helper_read_fails_the_node_with_expression_runtime_error()
    {
        var resolver = new CountingHelperResolver(throwRead: true);
        var definition = StoredWorkflowDocument.Read(Definition.Replace(
            "\"Predicate\":\"expr:${previous.src.v} > 3\"", "\"Predicate\":\"expr:${helper.k} > 0\""), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        definition.Nodes.Single(node => node.Id == "w").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        var observer = new StartedInputs();

        var outputs = await RunAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>>(), observer, definition, resolver);

        outputs["w"].Should().ContainKey("_error");
        ((Dictionary<string, object?>)outputs["w"]["_error"]!).Should().Contain("code", WorkflowErrorCodes.ExpressionRuntimeError);
        observer.Inputs["w"].Should().ContainKey("Label", "the node-start record retains bound inputs when helper reading fails");
    }

    [Fact]
    public async Task A_later_node_reads_a_helper_value_written_by_an_earlier_node()
    {
        var resolver = new CountingHelperResolver();
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var nodes = definition.Nodes.ToList();
        nodes.Insert(1, new WorkflowNodeDefinition
        {
            Id = "r", BlockTypeId = "helper-reader", Position = new WorkflowNodePosition { X = 60, Y = 0 },
            Config = new Dictionary<string, System.Text.Json.JsonElement> { ["Value"] = System.Text.Json.JsonSerializer.SerializeToElement("expr:${helper.k}") },
        });
        definition.Nodes = nodes;
        var edges = definition.Edges!.ToList();
        edges.Single(edge => edge.Id == "e0").TargetNodeId = "r";
        edges.Insert(1, new WorkflowConnectionDefinition { Id = "er", SourceNodeId = "r", TargetNodeId = "src", Mappings = System.Text.Json.JsonSerializer.SerializeToElement(new { }) });
        definition.Edges = edges;
        var waiter = definition.Nodes.Single(node => node.Id == "w");
        waiter.Config!["Label"] = System.Text.Json.JsonSerializer.SerializeToElement("expr:${helper.k}");
        resolver.Value = "before";

        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await RunAsync(seen, NullWorkflowNodeObserver.Instance, definition, resolver, () => resolver.Value = "written");

        seen["r"]["Value"].Should().Be("before");
        seen["w"]["Label"].Should().Be("written");
        resolver.ReadBatches.Should().HaveCount(2, "each node execution reads its own snapshot");
    }

    [Fact]
    public async Task A_filter_evaluating_many_items_uses_one_helper_snapshot()
    {
        var resolver = new CountingHelperResolver();
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var filter = definition.Nodes.Single(node => node.Id == "w");
        filter.BlockTypeId = "Filter";
        filter.Config!["Predicate"] = System.Text.Json.JsonSerializer.SerializeToElement("expr:${helper.k} > 0");

        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await RunAsync(seen, NullWorkflowNodeObserver.Instance, definition, resolver);

        seen["w"]["FilterEvaluations"].Should().Be(4);
        resolver.ReadBatches.Should().ContainSingle();
    }

    [Fact]
    public async Task A_later_expression_failure_leaves_both_inputs_as_text_in_the_started_node_record()
    {
        var observer = new StartedInputs();
        var definition = StoredWorkflowDocument.Read(Definition.Replace(
            "\"Label\":\"expr:\\\"a\\\" + \\\"b\\\"\"",
            "\"Label\":\"expr:\\\"a\\\" + \\\"b\\\"\",\"First\":\"expr:${previous.src.v} + 1\",\"Second\":\"expr:${previous.src.v} / 0\""), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        definition.Nodes.Single(node => node.Id == "w").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        var outputs = await RunAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>>(), observer, definition);

        outputs["w"].Should().ContainKey("_error");
        observer.Inputs["w"]["First"].Should().Be("expr:${previous.src.v} + 1");
        observer.Inputs["w"]["Second"].Should().Be("expr:${previous.src.v} / 0");
    }

    [Fact]
    public void The_compiler_types_the_text_against_T_and_admits_it()
    {
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());

        var diagnostics = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Catalogue(new())), CanvasSignature.Empty).Diagnostics;

        diagnostics.Should().BeEmpty(string.Join("; ", diagnostics.Select(d => d.RuleId + ": " + d.Message)));
    }

    private static RegisteredWorkflowBlocks Catalogue(
        Dictionary<string, IReadOnlyDictionary<string, object?>> seen, Action? afterSource = null, bool includeFilter = false) => new(
    [
        new Factory("src", seen, [], [new("v", "v", PortValueType.Int, false)], afterSource),
        new Factory("helper-reader", seen, [new("Value", "Value", PortValueType.String, false)], []),
        new Factory("waiter", seen,
            [new("Predicate", "Predicate", PortValueType.OfDeclaration("Expression<Boolean>"), false),
             new("Label", "Label", PortValueType.String, false),
             new("First", "First", PortValueType.Number, false),
             new("Second", "Second", PortValueType.Number, false)], []),
        new Factory("Filter", seen,
            [new("Predicate", "Predicate", PortValueType.OfDeclaration("Expression<Boolean>"), false),
             new("Label", "Label", PortValueType.String, false),
             new("First", "First", PortValueType.Number, false),
             new("Second", "Second", PortValueType.Number, false)], []),
    ]);

    private static async Task RunAsync(Dictionary<string, IReadOnlyDictionary<string, object?>> seen)
    {
        var definition = StoredWorkflowDocument.Read(Definition, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        await RunAsync(seen, NullWorkflowNodeObserver.Instance, definition);
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        Dictionary<string, IReadOnlyDictionary<string, object?>> seen, IWorkflowNodeObserver observer, WorkflowDefinition definition,
        IHelperResolver? helper = null, Action? afterSource = null)
    {
        var includeFilter = definition.Nodes.Any(node => node.BlockTypeId == "Filter");
        var blocks = Catalogue(seen, afterSource, includeFilter);
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, observer,
            helper ?? NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());
        var parameters = new Dictionary<string, object?>();
        var options = new CompilationOptions { HelperResolver = helper ?? NullHelperResolver.Instance };
        var compilation = await WorkflowCompiler.CompileAsync(definition,
            new CatalogueBlockShapeSource(Catalogue(seen, afterSource, includeFilter)), CanvasSignature.Empty, options);
        compilation.Diagnostics.Should().BeEmpty(string.Join("; ", compilation.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        return await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, compiled: CompiledWalk.Root(compilation.Program!));
    }

    private sealed class CountingHelperResolver(bool throwRead = false) : IHelperResolver
    {
        public List<IReadOnlyCollection<HelperPath>> ReadBatches { get; } = [];
        public int CheckCalls { get; private set; }
        public object? Value { get; set; } = 2L;
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            CheckCalls++;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, Value is string ? "String" : "Int")));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            ReadBatches.Add(paths.ToArray());
            if (throwRead)
                return ValueTask.FromException<IReadOnlyDictionary<HelperPath, object?>>(new InvalidOperationException("read failed"));
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(path => path, _ => Value));
        }
    }

    private sealed class ThrowingCheckResolver : IHelperResolver
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(new InvalidOperationException("check failed"));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class StartedInputs : IWorkflowNodeObserver
    {
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Inputs { get; } = [];
        public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken = default)
        { Inputs[nodeId] = inputs; return Task.CompletedTask; }
        public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs, long durationMs, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, string error, long durationMs, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Factory(
        string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen,
        BlockPortDefinition[] inputs, BlockPortDefinition[] outputs, Action? afterExecute = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IWorkflowBlockPlugin Create() => new Recording(id, seen, afterExecute);
    }

    private sealed class Recording(string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen, Action? afterExecute) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen[context.NodeId!] = inputs;
            if (id == "src") afterExecute?.Invoke();
            if (id == "Filter")
            {
                for (var item = 0; item < 4; item++)
                    _ = context.Expressions.Evaluate("Predicate", new Dictionary<string, object?>());
                seen[context.NodeId!] = new Dictionary<string, object?> { ["FilterEvaluations"] = 4 };
            }
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                id == "src" ? new Dictionary<string, object?> { ["v"] = 5L } : new Dictionary<string, object?>());
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
