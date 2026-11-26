using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed partial class HttpRequestBlockSecretTests
{
    private const string Cipher = "enc:AAA";
    private const string NameRulePlaintext = "N4MERULE-plaintext-1c9d";

    private static List<(string Key, string? Value)> RecordedHeaders(World world, string inputs = "Headers")
    {
        var step = world.Steps.Should().ContainSingle(s => s.NodeId == "http").Subject;
        var shown = JsonNode.Parse(step.InputsJson!)![inputs]!.AsObject();
        return [.. shown.Select(p => (p.Key, p.Value?.GetValue<string>()))];
    }

    private static IEnumerable<string> EverythingRecorded(World world) =>
        world.Steps.SelectMany(s => new[] { s.InputsJson, s.OutputsJson, s.ErrorMessage, s.ErrorStackTrace, s.ErrorJson }).OfType<string>()
            .Concat(world.Logs.Select(l => l.Message));

    [Fact]
    public async Task A_recorded_input_shows_a_Protected_and_a_name_rule_entry_as_stars_and_a_reference_as_written()
    {
        var codec = new FakeCodec { [Cipher] = "plain-AAA", ["ref:vault:db#pw"] = "plain-ref", ["ref:vault:db#backup"] = "plain-backup" };
        var world = new World(codec);

        await world.RunAsync(World.Configured(new Dictionary<string, string>
        {
            ["Accept"] = "text/plain",
            ["X-Custom"] = Cipher,
            ["Authorization"] = NameRulePlaintext,
            ["X-Ref"] = "ref:vault:db#pw",
            ["Authorization-Backup"] = "ref:vault:db#backup",
        }));

        RecordedHeaders(world).Should().Equal(
            ("Accept", "text/plain"),
            ("X-Custom", "***"),
            ("Authorization", "***"),
            ("X-Ref", "ref:vault:db#pw"),
            ("Authorization-Backup", "ref:vault:db#backup"));
        EverythingRecorded(world).Where(text => text.Contains(Cipher, StringComparison.Ordinal) || text.Contains(NameRulePlaintext, StringComparison.Ordinal))
            .Should().BeEmpty("a recorded input holds no ciphertext and no name-rule plaintext");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_node_that_fails_records_its_input_masked_too(bool emitError)
    {
        var codec = new FakeCodec { Failure = new InvalidOperationException("vault down") };
        var world = new World(codec);
        var scenario = World.Configured(
            new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext, ["Accept"] = "a" }, emitError);

        try
        {
            await world.RunAsync(scenario);
        }
        catch (WorkflowNodeExecutionException)
        {
            emitError.Should().BeFalse("only a node with no error policy throws");
        }

        RecordedHeaders(world).Should().Equal(("X-Custom", "***"), ("Authorization", "***"), ("Accept", "a"));
        EverythingRecorded(world).Where(text => text.Contains(Cipher, StringComparison.Ordinal) || text.Contains(NameRulePlaintext, StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_Protected_entry_that_arrived_by_a_wire_is_recorded_as_stars()
    {
        var world = new World(new FakeCodec { [Cipher] = "plain-AAA" });

        await world.RunAsync(World.Wired(new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Accept"] = "a" }));

        RecordedHeaders(world).Should().Equal(("X-Custom", "***"), ("Accept", "a"));
    }

    [Fact]
    public async Task A_key_holding_a_colon_shows_nothing_after_it_whether_the_name_rule_matches_or_not()
    {
        var world = new World(new FakeCodec());

        await world.RunAsync(World.Wired(
            new Dictionary<string, string> { ["X-Note: Bearer abc"] = "v", ["Authorization: Bearer xyz"] = "", ["Accept"] = "a" }, emitError: true));

        RecordedHeaders(world).Should().Equal(("X-Note:***", "v"), ("Authorization:***", "***"), ("Accept", "a"));
        world.Steps.Where(s => s.NodeId == "http").SelectMany(s => new[] { s.InputsJson, s.OutputsJson, s.ErrorMessage, s.ErrorStackTrace, s.ErrorJson }).OfType<string>()
            .Should().NotContain(text => text.Contains("Bearer", StringComparison.Ordinal), "the source node's own outputs are the test fixture's");
    }

    [Fact]
    public async Task With_no_codec_a_name_rule_entry_is_still_stars_and_nothing_else_is_taken_for_a_ciphertext()
    {
        var world = new World(codec: null);

        await world.RunAsync(World.Configured(new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext }));

        RecordedHeaders(world).Should().Equal(("X-Custom", Cipher), ("Authorization", "***"));
    }

    [Fact]
    public async Task An_input_that_does_not_declare_SecretValues_is_masked_by_the_name_rule_only()
    {
        var world = new World(new FakeCodec());
        world.Probe = (_, _) => Task.CompletedTask;

        await world.RunProbeAsync(
            new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext, ["Accept"] = "a" },
            other: new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext, ["Accept: x"] = "b" });

        RecordedHeaders(world).Should().Equal(("X-Custom", "***"), ("Authorization", "***"), ("Accept", "a"));
        RecordedHeaders(world, "Other").Should().Equal(("X-Custom", Cipher), ("Authorization", "***"), ("Accept: x", "b"));
    }

    [Fact]
    public async Task The_debug_hook_is_handed_no_secret_entry()
    {
        var hook = new CapturingHook();
        var world = new World(new FakeCodec { [Cipher] = "plain-AAA" }) { DebugHook = hook };

        await world.RunAsync(World.Configured(new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext }));

        hook.Seen.Should().NotBeEmpty();
        hook.Seen.Where(text => text.Contains(Cipher, StringComparison.Ordinal) || text.Contains(NameRulePlaintext, StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_node_observer_is_handed_stars_for_each_masked_entry_and_no_ciphertext_or_plaintext(bool fail)
    {
        var observer = new CapturingObserver();
        var codec = new FakeCodec { [Cipher] = "plain-AAA", Failure = fail ? new InvalidOperationException("vault down") : null };
        var world = new World(codec) { NodeObserver = observer };

        await world.RunAsync(World.Configured(
            new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext, ["Accept"] = "a" },
            emitError: true));

        observer.Seen.Should().NotBeEmpty();
        observer.Seen.Where(text => text.Contains(Cipher, StringComparison.Ordinal) || text.Contains(NameRulePlaintext, StringComparison.Ordinal))
            .Should().BeEmpty("an observer is a host-visible surface");
        observer.Inputs.Should().OnlyContain(inputs => ((IReadOnlyDictionary<string, string>)inputs["Headers"])["X-Custom"] == "***"
            && ((IReadOnlyDictionary<string, string>)inputs["Headers"])["Accept"] == "a");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(NodeErrorPolicy.FailWorkflow, false)]
    [InlineData(NodeErrorPolicy.ContinueWithDefault, false)]
    [InlineData(NodeErrorPolicy.EmitError, false)]
    [InlineData(NodeErrorPolicy.Skip, false)]
    public async Task The_node_observer_of_a_node_that_throws_is_handed_stars_whatever_the_error_policy_does_with_it(NodeErrorPolicy? policy, bool escapes)
    {
        var observer = new CapturingObserver();
        var world = new World(new FakeCodec()) { NodeObserver = observer };
        world.Probe = (_, _) => throw (escapes ? new InvalidWorkflowDefinitionException("boom") : new InvalidOperationException("boom"));

        var run = () => world.RunProbeAsync(
            new Dictionary<string, string> { ["X-Custom"] = Cipher, ["Authorization"] = NameRulePlaintext, ["Accept"] = "a" }, policy: policy);
        if (policy is NodeErrorPolicy.FailWorkflow or null)
            await run.Should().ThrowAsync<Exception>();
        else
            await run();

        observer.Inputs.Should().HaveCount(2, "the start and the end of the node are both told");
        observer.Seen.Where(text => text.Contains(Cipher, StringComparison.Ordinal) || text.Contains(NameRulePlaintext, StringComparison.Ordinal))
            .Should().BeEmpty("an observer is a host-visible surface");
        observer.Inputs.Should().OnlyContain(inputs => ((IReadOnlyDictionary<string, string>)inputs["Headers"])["X-Custom"] == "***"
            && ((IReadOnlyDictionary<string, string>)inputs["Headers"])["Accept"] == "a");
    }

    [Fact]
    public async Task The_node_observer_is_handed_a_wired_key_holding_a_colon_cut_after_it()
    {
        var observer = new CapturingObserver();
        var world = new World(new FakeCodec()) { NodeObserver = observer };

        await world.RunAsync(World.Wired(new Dictionary<string, string> { ["X-Note: Bearer abc"] = "v", ["Accept"] = "a" }, emitError: true));

        observer.Seen.Where(text => text.Contains("Bearer", StringComparison.Ordinal)).Should().BeEmpty();
        observer.Inputs.Should().OnlyContain(inputs => ((IReadOnlyDictionary<string, string>)inputs["Headers"])["X-Note:***"] == "v");
    }

    [Fact]
    public void A_passthrough_of_a_secret_capable_input_is_refused_at_compile_and_one_of_another_input_is_not()
    {
        var world = new World(new FakeCodec());
        var scenario = World.Configured(new Dictionary<string, string> { ["X-Custom"] = Cipher });

        world.Diagnose(scenario, "Headers").Should().ContainSingle(d => d.RuleId == "secret-input-passthrough")
            .Which.NodeId.Should().Be("http");
        world.Diagnose(scenario, "Url").Should().NotContain(d => d.RuleId == "secret-input-passthrough");
    }

    private sealed class CapturingObserver : IWorkflowNodeObserver
    {
        public List<string> Seen { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> Inputs { get; } = [];

        private void Take(string nodeId, IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?>? outputs = null)
        {
            if (nodeId != "http")
                return;
            Inputs.Add(inputs);
            Seen.Add(JsonSerializer.Serialize(inputs));
            if (outputs is not null)
                Seen.Add(JsonSerializer.Serialize(outputs));
        }

        public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken = default)
        {
            Take(nodeId, inputs);
            return Task.CompletedTask;
        }

        public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs, long durationMs, CancellationToken cancellationToken = default)
        {
            Take(nodeId, inputs, outputs);
            return Task.CompletedTask;
        }

        public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, string error, long durationMs, CancellationToken cancellationToken = default)
        {
            Take(nodeId, inputs);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHook : IWorkflowDebugHook
    {
        public CancellationToken KillToken => CancellationToken.None;

        public List<string> Seen { get; } = [];

        public Task OnNodeAsync(
            string nodeId, IReadOnlyDictionary<string, object?> inputs,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs, CancellationToken cancellationToken)
        {
            Seen.Add(JsonSerializer.Serialize(inputs));
            Seen.Add(JsonSerializer.Serialize(allOutputs));
            return Task.CompletedTask;
        }

        public Task OnNodeCompletedAsync(string nodeId, IReadOnlyDictionary<string, object?> outputs, CancellationToken cancellationToken)
        {
            Seen.Add(JsonSerializer.Serialize(outputs));
            return Task.CompletedTask;
        }
    }
}
