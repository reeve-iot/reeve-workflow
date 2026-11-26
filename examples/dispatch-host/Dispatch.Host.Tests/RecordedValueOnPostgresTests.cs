using System.Text.Json;
using FluentAssertions;
using Npgsql;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RecordedValueOnPostgresTests(ClusterBackends backends)
{
    public sealed record Inner(decimal Price, DayOfWeek Day, int[] Tags);

    public sealed record Outer(string Name, List<Inner> Items, Dictionary<string, object?> Extra);

    private const string Nested =
        """{"z":{"y":[1,2.50,1e20,1.0,{"b":null,"a":"\u0142\u2028<>\"q\""}],"x":true},"a":[[],{}],"n":12345678901234567890,"k2":"\ud83d\ude00"}""";

    private static Dictionary<string, object?> Values() => new()
    {
        ["record"] = new Outer("o", [new Inner(1.5m, DayOfWeek.Monday, [1, 2])], new() { ["j"] = Element() }),
        ["element"] = Element(),
        ["map"] = new Dictionary<string, object?> { ["b"] = 1.0, ["a"] = new List<object?> { 9007199254740993L, 0.1, "x" } },
        ["list"] = new List<object?> { new Dictionary<string, object?> { ["k"] = "v" }, 2 },
    };

    private static JsonElement Element() => JsonDocument.Parse(Nested).RootElement.Clone();

    [Fact]
    public async Task An_object_a_run_records_in_every_namespace_comes_back_from_jsonb_as_the_same_json()
    {
        var json = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
        {
            VariablesJson = "{}",
            PreviousOutputsJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["src"] = Values() }, WorkflowGraphHelpers.JsonOptions),
            TriggerEnvelopeJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [TriggerEnvelope.TriggerIdKey] = Guid.NewGuid(),
                [TriggerEnvelope.FiredAtKey] = DateTimeOffset.UnixEpoch,
                [TriggerEnvelope.PayloadKey] = Values(),
            }, WorkflowGraphHelpers.JsonOptions),
            ExecutionStateId = null,
        }, WorkflowGraphHelpers.JsonOptions);
        var written = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(json, WorkflowGraphHelpers.JsonOptions)!;

        var stored = await ThroughJsonbAsync(json);
        var state = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(stored, WorkflowGraphHelpers.JsonOptions)!;

        foreach (var (ns, before, after) in new[]
                 {
                     ("previous", written.PreviousOutputsJson, state.PreviousOutputsJson),
                     ("trigger", written.TriggerEnvelopeJson, state.TriggerEnvelopeJson),
                 })
        {
            System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(after), System.Text.Json.Nodes.JsonNode.Parse(before))
                .Should().BeTrue($"{ns} comes back from jsonb as it was written");
        }
    }

    private async Task<string> ThroughJsonbAsync(string json)
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_recorded_value_jsonb");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE TABLE payloads (payload jsonb NOT NULL)", connection))
            await create.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand("INSERT INTO payloads (payload) VALUES (@p::jsonb)", connection))
        {
            insert.Parameters.AddWithValue("p", json);
            await insert.ExecuteNonQueryAsync();
        }
        await using var select = new NpgsqlCommand("SELECT payload::text FROM payloads", connection);
        return (string)(await select.ExecuteScalarAsync())!;
    }
}
