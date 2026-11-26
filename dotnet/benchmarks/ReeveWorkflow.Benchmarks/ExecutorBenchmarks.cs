using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(1, 3, 10)]
public partial class ExecutorBenchmarks
{
    private const int LoopItems = 1000;
    private const int ChainLength = 50;

    private BenchmarkWorld? world;
    private WorkflowEngine? engine;
    private Guid iterateConstantId;
    private Guid iterateExpressionId;
    private Guid leafChainId;
    private Guid relayLiteralChainId;
    private Guid relayWiredChainId;

    [GlobalSetup]
    public async Task Setup()
    {
        world = new BenchmarkWorld();
        engine = world.Engine();
        SetupEngineScenarios();
        await SetupInputResolution();
        SetupWaker();
        SetupExpressions();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        engine?.Dispose();
    }

    [Benchmark(Description = "Scenario 1: IterateOver 1000 items (body input is a literal)")]
    public Task<IReadOnlyDictionary<string, object?>> IterateWithConstant() =>
        engine!.ExecuteAsync(iterateConstantId, null, null);

    [Benchmark(Description = "Scenario 2: IterateOver 1000 items (body input is an expr: over the item)")]
    public Task<IReadOnlyDictionary<string, object?>> IterateWithExpression() =>
        engine!.ExecuteAsync(iterateExpressionId, null, null);

    [Benchmark(Description = "Scenario 3a: 50-node chain, Leaf blocks, no ports, no wires")]
    public Task<IReadOnlyDictionary<string, object?>> ChainedNodes() =>
        engine!.ExecuteAsync(leafChainId, null, null);

    [Benchmark(Description = "Scenario 3b: 50-node chain, Relay blocks, 5 literal inputs per node, no wires")]
    public Task<IReadOnlyDictionary<string, object?>> RelayChainLiterals() =>
        engine!.ExecuteAsync(relayLiteralChainId, null, null);

    [Benchmark(Description = "Scenario 3c: 50-node chain, Relay blocks, 3 wires + 1 literal + 1 expr: per node")]
    public Task<IReadOnlyDictionary<string, object?>> RelayChainWired() =>
        engine!.ExecuteAsync(relayWiredChainId, null, null);

    internal Func<Task> IterateWithConstantOver(int itemCount)
    {
        var id = world!.AddWorkflow(IterateOverWorkflow(2, itemCount));
        return () => engine!.ExecuteAsync(id, null, null);
    }

    private void SetupEngineScenarios()
    {
        iterateConstantId = world!.AddWorkflow(IterateOverWorkflow(2));
        iterateExpressionId = world.AddWorkflow(IterateOverWorkflow("expr:${previous.in.Item} * 2"));
        leafChainId = world.AddWorkflow(LeafChain(ChainLength));
        relayLiteralChainId = world.AddWorkflow(RelayChain(ChainLength, wired: false));
        relayWiredChainId = world.AddWorkflow(RelayChain(ChainLength, wired: true));

        var (count, sum, _) = RunRecording(iterateConstantId, "V");
        Require(count == LoopItems && sum == 2.0 * LoopItems, $"constant loop: {count} bodies, V summed to {sum}");
        (count, sum, _) = RunRecording(iterateExpressionId, "V");
        Require(count == LoopItems && sum == 2.0 * (LoopItems * (LoopItems - 1) / 2),
            $"expression loop: {count} bodies, V summed to {sum}");

        var last = $"n{ChainLength - 1}";
        (count, _, var seen) = RunRecording(relayLiteralChainId, "A", last);
        Require(count == ChainLength && Num(seen, "A") == 0 && Num(seen, "Calc") == 1,
            $"literal chain: {count} nodes, last saw A={Num(seen, "A")} Calc={Num(seen, "Calc")}");

        (count, _, seen) = RunRecording(relayWiredChainId, "A", last);
        Require(count == ChainLength && Num(seen, "A") == ChainLength - 1 && Num(seen, "B") == ChainLength - 1
            && Num(seen, "C") == ChainLength - 1 && Num(seen, "Calc") == 1 && Equals(seen["Label"], "relay"),
            $"wired chain: {count} nodes, last saw A={Num(seen, "A")} B={Num(seen, "B")} C={Num(seen, "C")} "
            + $"Calc={Num(seen, "Calc")} Label={seen["Label"]}");

        engine!.ExecuteAsync(leafChainId, null, null).GetAwaiter().GetResult();
    }

    private (int Count, double Sum, IReadOnlyDictionary<string, object?> Last) RunRecording(
        Guid workflowId, string sumInput, string? lastNodeId = null)
    {
        var count = 0;
        var sum = 0.0;
        IReadOnlyDictionary<string, object?> last = new Dictionary<string, object?>();
        world!.Probe = (nodeId, inputs) =>
        {
            count++;
            sum += inputs.TryGetValue(sumInput, out var value) && value is not null ? Convert.ToDouble(value) : 0;
            if (lastNodeId is null || lastNodeId == nodeId)
                last = new Dictionary<string, object?>(inputs);
        };
        try
        {
            engine!.ExecuteAsync(workflowId, null, null).GetAwaiter().GetResult();
        }
        finally
        {
            world.Probe = null;
        }
        return (count, sum, last);
    }

    private static double Num(IReadOnlyDictionary<string, object?> inputs, string name) =>
        inputs.TryGetValue(name, out var value) && value is not null ? Convert.ToDouble(value) : double.NaN;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Benchmark setup assertion failed: " + message);
    }

    private static JsonObject Node(
        string id, string blockTypeId, int x = 0, JsonObject? subCanvases = null, JsonObject? configuration = null)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["blockTypeId"] = blockTypeId,
            ["position"] = new JsonObject { ["x"] = x, ["y"] = 0 },
            ["configuration"] = configuration ?? new JsonObject()
        };
        if (subCanvases is not null)
            node["subCanvases"] = subCanvases;
        return node;
    }

    private static JsonObject Edge(string source, string target, JsonObject? mappings = null) =>
        new()
        {
            ["id"] = $"{source}-{target}",
            ["sourceNodeId"] = source,
            ["targetNodeId"] = target,
            ["mappings"] = mappings ?? new JsonObject()
        };

    private static JsonObject Literal(JsonNode? value) => new() { ["type"] = "literal", ["literalValue"] = value };

    private static JsonObject Wire(string output) => new() { ["type"] = "direct", ["sourceOutputName"] = output };

    private static string Definition(JsonArray nodes, JsonArray edges) =>
        new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString();

    private static string IterateOverWorkflow(JsonNode bodyInput, int itemCount = LoopItems)
    {
        var items = new JsonArray();
        for (var i = 0; i < itemCount; i++)
            items.Add(i);

        var loop = Node("iterate", "IterateOver", 100, new JsonObject
        {
            ["Body"] = new JsonObject
            {
                ["nodes"] = new JsonArray(Node("in", "WorkflowInput"), Node("leaf", BenchmarkWorld.Echo)),
                ["edges"] = new JsonArray(Edge("in", "leaf", new JsonObject { ["V"] = Literal(bodyInput) }))
            }
        });

        return Definition(
            new JsonArray(Node("start", "WorkflowInput"), loop),
            new JsonArray(Edge("start", "iterate", new JsonObject { ["Collection"] = Literal(items) })));
    }

    private static string LeafChain(int length)
    {
        var nodes = new JsonArray(Node("start", "WorkflowInput"));
        var edges = new JsonArray();
        var previous = "start";
        for (var i = 0; i < length; i++)
        {
            var id = $"n{i}";
            nodes.Add(Node(id, "Leaf", i * 50));
            edges.Add(Edge(previous, id));
            previous = id;
        }
        return Definition(nodes, edges);
    }

    private static string RelayChain(int length, bool wired)
    {
        var nodes = new JsonArray(Node("start", "WorkflowInput"));
        var edges = new JsonArray();
        var previous = "start";
        for (var i = 0; i < length; i++)
        {
            var id = $"n{i}";
            nodes.Add(Node(id, BenchmarkWorld.Relay, i * 50));
            var mappings = new JsonObject();
            if (wired && i > 0)
            {
                mappings["A"] = Wire("a");
                mappings["B"] = Wire("b");
                mappings["C"] = Wire("c");
                mappings["Calc"] = Literal($"expr:${{previous.{previous}.a}} - ${{previous.{previous}.b}} + 1");
            }
            else
            {
                mappings["A"] = Literal(0);
                mappings["B"] = Literal(0);
                mappings["C"] = Literal(0);
                mappings["Calc"] = Literal(1);
            }
            mappings["Label"] = Literal("relay");
            edges.Add(Edge(previous, id, mappings));
            previous = id;
        }
        return Definition(nodes, edges);
    }
}
