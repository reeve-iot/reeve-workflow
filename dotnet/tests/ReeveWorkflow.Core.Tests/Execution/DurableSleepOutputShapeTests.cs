using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepOutputShapeTests
{
    public sealed record Reading(string Name, double Value);

    [Fact]
    public async Task Earlier_outputs_read_after_a_park_arrive_in_the_types_their_terms_give_them()
    {
        var (inProcess, inProcessSlept) = await SinkInputsAsync(park: false);
        var (parked, parkedSlept) = await SinkInputsAsync(park: true);

        inProcess["when"].Should().BeOfType<DateTimeOffset>();
        inProcess["count"].Should().BeOfType<long>("an Int port carries a long");
        inProcess["id"].Should().BeOfType<Guid>();
        inProcess["reading"].Should().BeOfType<Reading>();
        inProcess["ratio"].Should().BeOfType<double>();

        Carried.Select(name => parked[name]?.GetType()).Should().Equal(
            Carried.Select(name => inProcess[name]?.GetType()), "a resume reads each value back by its port's term");
        parkedSlept.Should().Be(inProcessSlept, "the sleep node's own outputs are the same either way");
    }

    private static readonly string[] Carried = ["when", "count", "id", "reading", "ratio"];

    private static readonly Dictionary<string, PortValueType> Terms = new()
    {
        ["when"] = PortValueType.DateTime, ["count"] = PortValueType.Int, ["id"] = PortValueType.Of("Ident"),
        ["reading"] = PortValueType.Of("Gauge"), ["ratio"] = PortValueType.Number,
    };

    private static readonly CompilationOptions Options = new()
    {
        TypeRegistry = new WorkflowTypeRegistry(
        [
            Persisted("Ident", typeof(Guid)),
            Persisted("Gauge", typeof(Reading)),
        ]),
    };

    private static WorkflowTypeRecord Persisted(string name, Type clr) => new(name, clr)
    {
        JsonTypeInfo = StrictJson.Info(clr),
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
    };

    private static List<BlockPortDefinition> Ports(params string[] names) => [.. names.Select(name => LambdaFactory.Port(name, Terms[name]))];

    private static async Task<(IReadOnlyDictionary<string, object?> Inputs, object? Slept)> SinkInputsAsync(bool park)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = park ? TimeSpan.FromMilliseconds(1) : null };
        world.SuspensionSeam = world.Suspensions;
        world.CompilationOptions = Options;
        var seen = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            "test.src" => new LambdaFactory(id, _ => new Dictionary<string, object?>
            {
                ["when"] = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.FromHours(2)),
                ["count"] = 5, ["id"] = Guid.NewGuid(), ["reading"] = new Reading("t", 21.5), ["ratio"] = 2.0
            }, outputs: Ports(Carried)),
            "test.sink" => new LambdaFactory(id, inputs => { seen.Enqueue(inputs); return new Dictionary<string, object?>(); },
                inputs: [.. Ports(Carried), LambdaFactory.Port("slept", PortValueType.Int)]),
            _ => null
        };
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "src", blockTypeId = "test.src", position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType, configuration = new { DurationMs = 30 }, position = new { x = 0, y = 0 } },
                new { id = "sink", blockTypeId = "test.sink", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = "src", mappings = new { } },
                new { id = "b", sourceNodeId = "src", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
                new
                {
                    id = "c", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = "sink",
                    mappings = DurableSleepWorld.Direct("slept", "DurationMs")
                },
                new
                {
                    id = "d", sourceNodeId = "src", targetNodeId = "sink",
                    mappings = new[] { "when", "count", "id", "reading", "ratio" }
                        .ToDictionary(name => name, name => (object)new { type = "direct", sourceOutputName = name })
                }
            }
        }));
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        if (park)
        {
            var row = world.Suspensions.SingleRow;
            var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
            (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        }

        var inputs = seen.Should().ContainSingle().Subject;
        return (inputs, inputs["slept"]);
    }
}
