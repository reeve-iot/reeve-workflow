using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class SuspensionInputParameterTests
{
    private const string Lookup = "test.lookup";
    private const string Greet = "test.greet";
    private const string Wait = "test.wait";

    public enum Parked { NotAtAll, OnASleep, OnATemplate }

    [Theory]
    [InlineData(Parked.NotAtAll)]
    [InlineData(Parked.OnASleep)]
    [InlineData(Parked.OnATemplate)]
    public async Task A_resumed_run_reads_the_parameters_it_was_called_with(Parked parked)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = parked == Parked.OnASleep ? TimeSpan.FromMinutes(1) : null };
        world.SuspensionSeam = world.Suspensions;
        var greeted = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Lookup => new LambdaFactory(id, _ => new Dictionary<string, object?> { ["who"] = "bob" }),
            Greet => new LambdaFactory(id, inputs => { greeted.Enqueue(inputs); return new Dictionary<string, object?>(); }),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(Definition(parked), inputParametersJson: WhoIsAParameter);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["who"] = "alice" });
        if (parked != Parked.NotAtAll)
        {
            var row = world.Suspensions.SingleRow;
            var outcome = parked == Parked.OnASleep
                ? WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc)
                : """{"outcome":"timedOut"}""";
            var context = (await world.Suspensions.TryResolveAsync(row.Id,
                parked == Parked.OnASleep ? WorkflowSuspensionStatus.Resumed : WorkflowSuspensionStatus.TimedOut,
                outcome, DateTimeOffset.UtcNow, default))!;
            (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        }

        greeted.Should().ContainSingle().Which["to"].Should().Be("alice");
    }

    [Theory]
    [InlineData("alice", 1)]
    [InlineData("bob", 0)]
    public async Task A_waker_reads_a_template_against_the_parameters_the_run_was_called_with(string compareTo, int woken)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id switch
        {
            Lookup => new LambdaFactory(id, _ => new Dictionary<string, object?> { ["who"] = "bob" }),
            Greet => new LambdaFactory(id, _ => new Dictionary<string, object?>()),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(
            Definition(Parked.OnATemplate, $"${{parameter.who}} == \"{compareTo}\""), inputParametersJson: WhoIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["who"] = "alice" });

        var waker = new WorkflowSuspensionWaker(new EveryParkedRow(world.Suspensions), world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new Core.Compilation.WorkflowCompilationService(world.Blocks, world.RunScopes), new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero },
            scopes: world.RunScopes);

        (await waker.WakeAndWalkAsync()).Should().Be(woken, "the run was called with who = alice, and a node output named who is not a parameter");
    }

    [Fact]
    public void A_wait_payload_carries_the_parameters_once()
    {
        var parameters = new Dictionary<string, object?> { ["body"] = new string('x', 100 * 1024) };
        var executed = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = parameters
        };

        var payload = WorkflowExecutionHelpers.BuildSuspensionStatePayload(executed, TestPrograms.Untyped, null, null, null);

        System.Text.Encoding.UTF8.GetByteCount(payload).Should().BeLessThan(256 * 1024,
            "the variables snapshot and the input node's outputs already hold them; a third copy would push payloads past the cap");
    }

    [Fact]
    public async Task A_wait_whose_parameters_fit_the_cap_on_5_1_still_parks_with_no_threshold()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = new CappedAt256KiB(world.Suspensions);
        world.Blocks.Extra = id => id switch
        {
            Lookup => new LambdaFactory(id, _ => new Dictionary<string, object?>()),
            Greet => new LambdaFactory(id, _ => new Dictionary<string, object?>()),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(Definition(Parked.OnATemplate), inputParametersJson: """[{"name":"who","type":"String"},{"name":"body","type":"String"}]""");
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["body"] = new string('x', 100 * 1024) });

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        world.Suspensions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task A_row_written_before_the_input_node_was_named_resumes_with_the_parameters_from_its_input_node()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        var greeted = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Lookup => new LambdaFactory(id, _ => new Dictionary<string, object?> { ["who"] = "bob" }),
            Greet => new LambdaFactory(id, inputs => { greeted.Enqueue(inputs); return new Dictionary<string, object?>(); }),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(Definition(Parked.OnATemplate), inputParametersJson: WhoIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["who"] = "alice" });
        var row = world.Suspensions.SingleRow;
        var old = System.Text.Json.Nodes.JsonNode.Parse(row.StatePayloadJson)!.AsObject();
        old.Remove("InputNodeId");
        old.Remove("InputParametersJson");
        world.Suspensions.Rows[0] = row with { StatePayloadJson = old.ToJsonString() };

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.TimedOut,
            """{"outcome":"timedOut"}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        greeted.Should().ContainSingle().Which["to"].Should().Be("alice");
    }

    private sealed class CappedAt256KiB(MemorySuspensions inner) : IWorkflowSuspensionStore
    {
        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(request.StatePayloadJson);
            return bytes > 256 * 1024
                ? throw new SuspendPayloadTooLargeException(bytes)
                : inner.SuspendAsync(request, cancellationToken);
        }

        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner.CancelByExecutionAsync(executionLogId, cancellationToken);
        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }
    }

    internal sealed class EveryParkedRow(MemorySuspensions inner) : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, inner.Rows
                .Where(r => r.Status == WorkflowSuspensionStatus.Suspended)
                .Select(r => new WorkflowSuspensionCandidate(r.Id, r.ExecutionLogId, r.WorkflowId, r.BlockId, r.Kind,
                    r.ConfigJson, r.StatePayloadJson, r.SuspendedAtUtc, r.TimeoutAtUtc)).ToList(), 0));

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }

    private const string WhoIsAParameter = "[{\"name\":\"who\",\"type\":\"String\"}]";

    private static string Definition(Parked parked, string condition = "false") => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "lookup", blockTypeId = Lookup, position = new { x = 0, y = 0 }, configuration = new { } },
            parked == Parked.OnATemplate
                ? new { id = "pause", blockTypeId = Wait, configuration = new Dictionary<string, object?> { [TemplateWaits.Input] = "expr:" + condition }, position = new { x = 0, y = 0 } }
                : new
                {
                    id = "pause", blockTypeId = DurableSleepWorld.SleepType,
                    configuration = new Dictionary<string, object?> { ["DurationMs"] = parked == Parked.OnASleep ? 600_000 : 20 }, position = new { x = 0, y = 0 }
                },
            new { id = "greet", blockTypeId = Greet, configuration = new Dictionary<string, object?> { ["to"] = "expr:${parameter.who}" }, position = new { x = 0, y = 0 } }
        },
        edges = new object[]
        {
            new { id = "a", sourceNodeId = "start", targetNodeId = "lookup", mappings = new { } },
            new { id = "b", sourceNodeId = "lookup", targetNodeId = "pause", mappings = new { } },
            new { id = "c", sourceNodeId = "pause", targetNodeId = "greet", mappings = new { } }
        }
    });
}
