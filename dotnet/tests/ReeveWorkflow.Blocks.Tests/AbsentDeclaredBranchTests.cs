using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

namespace ReeveWorkflow.Blocks.Tests;

public sealed class AbsentDeclaredBranchTests
{
    private const string OpenedEntryOnly =
        """{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}}],"edges":[]}""";

    private const string OpenedWithExit =
        """{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}},"""
        + """{"id":"__workflow_output__","blockTypeId":"WorkflowOutput","position":{"x":800,"y":100},"configuration":{}}],"edges":[]}""";

    private static readonly Dictionary<string, Func<string, Scenario>> Scenarios = new(StringComparer.Ordinal)
    {
        ["IfElse"] = branch => new(new() { ["Condition"] = Json(branch == "Then") }),
        ["TryCatch"] = branch => new([], Throws: branch == "Catch" ? "Try" : null),
        ["WhileLoop"] = _ => new(new() { ["MaxIterations"] = Json(3) }),
        ["ForLoop"] = _ => new(new() { ["To"] = Json(2) }),
        ["IterateOver"] = _ => new(new() { ["Collection"] = Json(new[] { "a", "b" }) }),
        ["IfPresent"] = branch => new(branch == "Present" ? new() { ["Value"] = Json(1) } : []),
    };

    public static TheoryData<string, string, bool, bool, int> Cases()
    {
        var cases = new TheoryData<string, string, bool, bool, int>();
        foreach (var factory in Containers())
            foreach (var branch in factory.SubCanvasNames)
                foreach (var routed in factory.SupportsDeclaredOutputs ? [false, true] : new[] { false })
                    foreach (var othersDrawn in factory.SubCanvasNames.Count > 1 ? [true, false] : new[] { true })
                        foreach (var depth in (int[])[0, 1, 2])
                            cases.Add(factory.BlockTypeId, branch, routed, othersDrawn, depth);
        return cases;
    }

    [Fact]
    public void Every_container_the_package_ships_has_a_scenario_here()
    {
        Containers().Select(f => f.BlockTypeId).Should().BeEquivalentTo(Scenarios.Keys);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task An_absent_declared_branch_runs_compiles_and_compares_as_the_empty_branch_the_editor_writes(
        string blockTypeId, string branch, bool routed, bool othersDrawn, int depth)
    {
        var absent = Document(blockTypeId, branch, routed, othersDrawn, depth, opened: false);
        var opened = Document(blockTypeId, branch, routed, othersDrawn, depth, opened: true);

        var run = await Trace(opened);
        run.Should().Contain(l => l.StartsWith("ended ", StringComparison.Ordinal), "each scenario runs to its end");
        (await Trace(absent)).Should().Equal(run, "the run reads an absent declared branch as the empty one");
        var verdict = Verdict(opened);
        verdict.Should().OnlyContain(l => l.StartsWith("canvas ", StringComparison.Ordinal), "each scenario compiles");
        Verdict(absent).Should().Equal(verdict, "the compiler grades it as the empty one");
        StoredVersionComparison.Compare(new(1, absent, null, null), new(2, opened, null, null), Blocks(new Probe()))
            .Should().BeEquivalentTo(NoChange, "the two versions run alike");
        Whole(absent).Should().Be(Whole(opened), "a recovery answers for what the branch runs");
    }

    [Fact]
    public async Task A_container_below_a_container_of_its_own_id_aborts_the_run_and_is_not_refused()
    {
        var inner = new WorkflowNodeDefinition
        {
            Id = "twin", BlockTypeId = "ForLoop", Config = new() { ["To"] = Json(1) },
            SubCanvases = new(StringComparer.Ordinal) { ["Body"] = Read(OpenedEntryOnly) },
        };
        var outer = new WorkflowNodeDefinition
        {
            Id = "twin", BlockTypeId = "ForLoop", Config = new() { ["To"] = Json(1) },
            SubCanvases = new(StringComparer.Ordinal) { ["Body"] = Canvas("in", inner) },
        };
        var json = Canvas("start", outer).ToJson();

        Verdict(json).Should().OnlyContain(l => l.StartsWith("canvas ", StringComparison.Ordinal));
        (await Trace(json)).Should().Contain(l => l.Contains("Circular reference detected at node 'twin'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("body")]
    public async Task A_name_the_block_does_not_declare_in_that_case_still_fails_the_call(string called)
    {
        var caller = new WorkflowNodeDefinition { Id = "caller", BlockTypeId = CallerBlockFactory.Id, Config = new() { ["Call"] = Json(called) } };
        var declared = new WorkflowNodeDefinition { Id = "caller", BlockTypeId = CallerBlockFactory.Id, Config = new() { ["Call"] = Json("Body") } };

        (await Trace(Canvas("start", declared).ToJson())).Should().Contain(l => l.StartsWith("ended ", StringComparison.Ordinal));
        (await Trace(Canvas("start", caller).ToJson()))
            .Should().Contain(l => l.Contains($"Sub-canvas '{called}' not found on node 'caller'", StringComparison.Ordinal));
    }

    private static readonly Guid RunId = Guid.Parse("21a7e2a0-0000-4000-8000-000000000021");

    private static readonly WorkflowDiffResponse NoChange =
        new([], [], [], [], [], new([], [], []), [], new([], [], []));

    private static IEnumerable<IWorkflowBlockFactory> Containers()
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        return services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>().Where(f => f.SubCanvasNames.Count > 0);
    }

    private static IWorkflowBlockFactory Factory(string blockTypeId) =>
        Containers().Single(f => f.BlockTypeId == blockTypeId);

    private static string Document(string blockTypeId, string branch, bool routed, bool othersDrawn, int depth, bool opened)
    {
        var factory = Factory(blockTypeId);
        var scenario = Scenarios[blockTypeId](branch);
        var container = new WorkflowNodeDefinition { Id = "container", BlockTypeId = blockTypeId, Config = scenario.Config };
        if (routed)
        {
            container.OutputPorts =
            [
                new() { Name = "v", DisplayName = "v", Type = "String", Sources = factory.SubCanvasNames.ToDictionary(n => n, _ => "v") }
            ];
        }

        var branches = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var name in factory.SubCanvasNames)
        {
            var exit = routed || factory.GetSubCanvasOutputs(name).Count > 0;
            if (name != branch && othersDrawn)
                branches[name] = Drawn(name, throws: scenario.Throws == name, exit);
            else if (opened)
                branches[name] = Read(exit ? OpenedWithExit : OpenedEntryOnly);
        }
        container.SubCanvases = branches.Count > 0 ? branches : null;

        var top = container;
        for (var level = depth; level > 0; level--)
        {
            top = new WorkflowNodeDefinition
            {
                Id = $"wrap{level}", BlockTypeId = "IfElse", Config = new() { ["Condition"] = Json(true) },
                SubCanvases = new(StringComparer.Ordinal)
                {
                    ["Then"] = Canvas("in", top),
                    ["Else"] = Read(OpenedEntryOnly),
                },
            };
        }

        return Canvas("start", top).ToJson();
    }

    private static WorkflowDefinition Canvas(string entryId, WorkflowNodeDefinition node) => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = entryId, BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, node],
        Edges = [new WorkflowConnectionDefinition { Id = $"{entryId}-{node.Id}", SourceNodeId = entryId, TargetNodeId = node.Id, Mappings = JsonSerializer.SerializeToElement(new { }) }],
    };

    private static WorkflowDefinition Drawn(string mark, bool throws, bool exit)
    {
        var nodes = new List<WorkflowNodeDefinition>
        {
            new() { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new()
            {
                Id = "probe", BlockTypeId = ProbeBlockFactory.Id,
                Config = new() { ["Mark"] = Json(mark), ["Throw"] = Json(throws) },
            },
        };
        if (exit)
            nodes.Add(new() { Id = "out", BlockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId });
        return new()
        {
            Nodes = nodes,
            Edges = [new WorkflowConnectionDefinition { Id = "in-probe", SourceNodeId = "in", TargetNodeId = "probe", Mappings = JsonSerializer.SerializeToElement(new { }) }],
        };
    }

    private static WorkflowDefinition Read(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowGraphHelpers.JsonOptions)!;

    private static async Task<List<string>> Trace(string json)
    {
        var probe = new Probe();
        var steps = new List<WorkflowExecutionStep>();
        var logs = new List<WorkflowExecutionLogEntry>();
        string ending;
        try
        {
            var outputs = await Executor(Blocks(probe)).ExecuteGraphAsync(
                [], RunId, RunId, RunId, EmptyServices.Instance,
                logs, steps, WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
                isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
                triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                compiled: CompiledWalk.Root(WorkflowCompiler.Compile(Read(json), new CatalogueBlockShapeSource(Blocks(probe)), CanvasSignature.Empty).Program!));
            ending = "ended " + JsonSerializer.Serialize(outputs.OrderBy(p => p.Key, StringComparer.Ordinal), WorkflowGraphHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            ending = $"threw {ex.GetType().Name}: {ex.Message} / {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}";
        }

        return
        [
            .. probe.Marks.Select(m => "mark " + m),
            ending,
            .. steps.Select(s => $"step {s.NodeId}|{s.NodeType}|{s.Status}|{s.SortOrder}|{s.InputsJson}|{s.OutputsJson}|{s.ErrorMessage}"),
            .. logs.Select(l => $"log {l.NodeId}|{l.BlockTypeId}|{l.Level}|{l.Message}|{l.DataJson}|{l.IterationIndex}|{l.CallStackJson}"),
        ];
    }

    private static List<string> Verdict(string json)
    {
        var result = WorkflowCompiler.Compile(Read(json), new CatalogueBlockShapeSource(Blocks(new Probe())), CanvasSignature.Empty);
        return
        [
            .. result.Diagnostics
                .Select(d => $"{d.RuleId}|{d.NodeId}|{d.PortName}|{string.Join("/", d.CanvasPath.Select(s => s.NodeId + ":" + s.BranchName))}|{d.Message}")
                .Order(StringComparer.Ordinal),
            .. (result.Program?.Canvases ?? [])
                .Select(c => $"canvas {c.CanvasPath}|{string.Join(",", c.NodeOrder)}|"
                    + string.Join(",", c.Nodes.SelectMany(n => n.SubCanvases.Keys.Order(StringComparer.Ordinal).Select(k => n.NodeId + ":" + k))))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static bool Whole(string json) =>
        WorkflowIdempotence.Whole(Read(json).Nodes.Last(), Blocks(new Probe()));

    private static RegisteredWorkflowBlocks Blocks(Probe probe)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new ProbeBlockFactory(probe));
        services.AddSingleton<IWorkflowBlockFactory>(new CallerBlockFactory());
        return new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
    }

    private static WorkflowGraphExecutor Executor(IWorkflowBlocks blocks) =>
        new(blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(), new NullWorkflowNodeObserver(),
            NullHelperResolver.Instance);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed record Scenario(Dictionary<string, JsonElement> Config, string? Throws = null);

    private sealed class Probe
    {
        public List<string> Marks { get; } = [];
    }

    private sealed class ProbeBlockFactory(Probe probe) : IWorkflowBlockFactory
    {
        public const string Id = "test.probe";

        public string BlockTypeId => Id;
        public string DisplayName => "Probe";
        public string Description => "Records that it ran, then optionally fails.";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Continue", "Continue", PortValueType.Boolean, false)];

        public IWorkflowBlockPlugin Create() => new ProbeBlock(probe);
    }

    private sealed class ProbeBlock(Probe probe) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            probe.Marks.Add((string)inputs["Mark"]!);
            if (inputs.TryGetValue("Throw", out var fail) && fail is true)
                throw new InvalidOperationException("probe failed");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Continue"] = false });
        }
    }

    private sealed class CallerBlockFactory : IWorkflowBlockFactory
    {
        public const string Id = "test.caller";

        public string BlockTypeId => Id;
        public string DisplayName => "Caller";
        public string Description => "Calls one branch by name.";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];

        public IWorkflowBlockPlugin Create() => new CallerBlock();
    }

    private sealed class CallerBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            context.SubCanvasExecutor!.ExecuteAsync((string)inputs["Call"]!, inputs, cancellationToken);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
