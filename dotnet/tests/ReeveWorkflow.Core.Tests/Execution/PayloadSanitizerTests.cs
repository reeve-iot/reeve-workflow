using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class PayloadSanitizerTests
{
    [Fact]
    public void Nested_secrets_inside_arrays_are_masked_without_changing_siblings()
    {
        var payload = new Dictionary<string, object?>
        {
            ["items"] = new object?[]
            {
                new Dictionary<string, object?> { ["secretValue"] = "hidden", ["label"] = "visible" }
            }
        };

        var json = PayloadSanitizer.SerializeAndSanitize(payload, 256);

        json.Should().NotBeNull();
        var root = JsonNode.Parse(json!)!;
        root["items"]![0]!["secretValue"]!.GetValue<string>().Should().Be("***");
        root["items"]![0]!["label"]!.GetValue<string>().Should().Be("visible");
    }

    [Fact]
    public void A_sensitive_key_holding_a_colon_shows_nothing_after_the_colon_at_any_depth()
    {
        var payload = new Dictionary<string, object?>
        {
            ["Authorization: Bearer x"] = "",
            ["Headers"] = new Dictionary<string, string> { ["Authorization: Bearer y"] = "", ["Accept: text/plain"] = "ok" },
        };

        var json = PayloadSanitizer.SerializeAndSanitize(payload, 256)!;

        json.Should().NotContain("Bearer");
        var root = JsonNode.Parse(json)!;
        root["Authorization:***"]!.GetValue<string>().Should().Be("***");
        root["Headers"]!["Authorization:***"]!.GetValue<string>().Should().Be("***");
        root["Headers"]!["Accept: text/plain"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void Masked_keys_keep_their_position_among_the_others()
    {
        var payload = new Dictionary<string, object?>
        {
            ["Headers"] = new Dictionary<string, string>
            {
                ["Accept"] = "a", ["Authorization"] = "x", ["X-Note"] = "n", ["Cookie: s=1"] = "", ["X-Last"] = "l",
            },
        };

        var root = JsonNode.Parse(PayloadSanitizer.SerializeAndSanitize(payload, 256)!)!;

        root["Headers"]!.AsObject().Select(entry => entry.Key).Should()
            .Equal("Accept", "Authorization", "X-Note", "Cookie:***", "X-Last");
        root["Headers"]!["Authorization"]!.GetValue<string>().Should().Be("***");
        root["Headers"]!["X-Last"]!.GetValue<string>().Should().Be("l");
    }

    [Fact]
    public void Nested_secrets_in_json_values_are_masked_and_json_text_remains_text()
    {
        var value = JsonNode.Parse("{\"nested\":[{\"api_key\":\"hidden\",\"ok\":1}]}")!;
        var payload = new Dictionary<string, object?>
        {
            ["value"] = value,
            ["text"] = "{\"secret\":\"keep-as-text\"}"
        };

        var root = JsonNode.Parse(PayloadSanitizer.SerializeAndSanitize(payload, 256)!)!;

        root["value"]!["nested"]![0]!["api_key"]!.GetValue<string>().Should().Be("***");
        root["value"]!["nested"]![0]!["ok"]!.GetValue<int>().Should().Be(1);
        root["text"]!.GetValue<string>().Should().Be("{\"secret\":\"keep-as-text\"}");
    }

    [Fact]
    public void Secrets_beyond_the_walk_limit_are_not_returned_for_clr_or_json_nodes()
    {
        object? clr = "hidden";
        for (var i = 0; i < 80; i++)
            clr = new Dictionary<string, object?> { ["layer"] = new object?[] { clr } };
        JsonNode json = JsonValue.Create("hidden")!;
        for (var i = 0; i < 80; i++)
            json = new JsonObject { ["layer"] = new JsonArray(json) };

        var clrJson = PayloadSanitizer.SerializeAndSanitize(new Dictionary<string, object?> { ["value"] = clr }, 1_000_000);
        var nodeJson = PayloadSanitizer.SerializeAndSanitize(new Dictionary<string, object?> { ["value"] = json }, 1_000_000);

        clrJson.Should().NotContain("hidden");
        nodeJson.Should().NotContain("hidden");
        clrJson.Should().Contain("\"[unserializable]\"");
        nodeJson.Should().Contain("\"[unserializable]\"");
    }

    [Fact]
    public void Sanitize_http_never_returns_a_too_deep_json_body_as_raw_text()
    {
        var body = "{\"layer\":" + new string('[', 70) + "{\"token\":\"hidden\"}" + new string(']', 70) + "}";

        var json = PayloadSanitizer.SanitizeHttp(null, null, body, 1_000_000);

        json.Should().NotContain("hidden");
        json.Should().Contain("\"[unserializable]\"");
    }

    [Fact]
    public void Sanitize_http_keeps_the_text_fallback_for_invalid_json_bodies()
    {
        const string body = "{this is not valid JSON";

        var json = PayloadSanitizer.SanitizeHttp(null, null, body, 1_000_000);

        JsonNode.Parse(json!)!["body"]!.GetValue<string>().Should().Be(body);
    }

    [Fact]
    public void Sanitize_http_fails_closed_when_json_exceeds_the_finite_parse_limit()
    {
        var body = new string('[', 140) + "{\"token\":\"hidden\"}" + new string(']', 140);

        var json = PayloadSanitizer.SanitizeHttp(null, null, body, 1_000_000);

        JsonNode.Parse(json!)!["body"]!.GetValue<string>().Should().Be("[unserializable]");
        json.Should().NotContain("hidden");
    }

    [Fact]
    public void Truncation_runs_after_nested_secret_masking()
    {
        var payload = new Dictionary<string, object?>
        {
            ["details"] = new Dictionary<string, object?>
            {
                ["secret"] = new string('x', 500)
            },
            ["note"] = new string('n', 100)
        };

        var json = PayloadSanitizer.SerializeAndSanitize(payload, 8);

        json.Should().Contain("\"secret\":\"***\"");
        json.Should().Contain("\"note\":\"nnnnnnnn...[truncated]\"");
    }

    [Fact]
    public void Node_trace_input_and_output_json_masks_nested_values()
    {
        var executor = new WorkflowGraphExecutor(
            new EmptyBlocks(), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);
        var stepRows = new List<WorkflowExecutionStep>();
        var sortOrder = 0;
        var node = System.Text.Json.JsonSerializer.Deserialize<WorkflowNodeDefinition>(
            "{\"id\":\"n\",\"blockTypeId\":\"echo\"}", WorkflowGraphHelpers.JsonOptions)!;
        var inputs = new Dictionary<string, object?>
        {
            ["data"] = new object?[] { new Dictionary<string, object?> { ["token"] = "input-secret", ["ok"] = "in" } }
        };
        var outputs = new Dictionary<string, object?>
        {
            ["data"] = JsonNode.Parse("[{\"password\":\"output-secret\",\"ok\":\"out\"}]")
        };

        executor.AddExecutionStep(stepRows, ref sortOrder, Guid.NewGuid(), node,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, WorkflowExecutionStepStatus.Succeeded,
            inputs, outputs, null, null, null, WorkflowTraceMode.Full);

        var step = stepRows.Should().ContainSingle().Subject;
        var traceInputs = JsonNode.Parse(step.InputsJson!)!;
        traceInputs["data"]![0]!["token"]!.GetValue<string>().Should().Be("***");
        traceInputs["data"]![0]!["ok"]!.GetValue<string>().Should().Be("in");
        var traceOutputs = JsonNode.Parse(step.OutputsJson!)!;
        traceOutputs["data"]![0]!["password"]!.GetValue<string>().Should().Be("***");
        traceOutputs["data"]![0]!["ok"]!.GetValue<string>().Should().Be("out");
    }

    private sealed class EmptyBlocks : IWorkflowBlocks
    {
        public ReeveWorkflow.Abstractions.IWorkflowBlockFactory? Find(string blockTypeId) => null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<ReeveWorkflow.Abstractions.IWorkflowBlockFactory> GetAll() => [];
    }

}
