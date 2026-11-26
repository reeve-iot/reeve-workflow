using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class WorkflowExportDtoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Checkpoint_policy_survives_an_export_round_trip_and_old_exports_read_as_null()
    {
        const string policy = "{\"Mode\":\"EveryBlock\"}";
        var exported = new WorkflowExportDto("1", DateTimeOffset.UnixEpoch, "Ship", null, "{}", null, null)
        {
            CheckpointPolicyJson = policy,
        };

        var imported = JsonSerializer.Deserialize<WorkflowExportDto>(JsonSerializer.Serialize(exported, Json), Json)!;
        var oldExport = JsonSerializer.Deserialize<WorkflowExportDto>(
            """{"schemaVersion":"1","exportedAtUtc":"1970-01-01T00:00:00Z","name":"Ship","description":null,"definitionJson":"{}","inputParametersJson":null,"outputParametersJson":null}""",
            Json)!;

        imported.CheckpointPolicyJson.Should().Be(policy);
        imported.Name.Should().Be(exported.Name);
        oldExport.CheckpointPolicyJson.Should().BeNull();
    }
}
