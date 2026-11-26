using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class EditorAndExecutorTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_workflow_written_through_the_editor_api_is_run_by_the_executor()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_api");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var created = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Editor-written dispatch",
            definitionJson = FreightDesk.AssignCarrierDefinition("SHP-9001", "Overnight", 0),
            outputParametersJson = FreightDesk.AssignCarrierOutputParametersJson,
            mode = nameof(WorkflowMode.Parallel),
            maxConcurrentRuns = 5,
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created, because: created.Body);
        created.Location.Should().EndWith(created.Json.GetProperty("id").GetString(),
            because: "a 201 that does not say where the thing is makes a caller guess the address");

        var workflowId = created.Json.GetProperty("id").GetGuid();
        var run = await desk.ExecuteAsync(workflowId);

        run.Status.Should().Be(HttpStatusCode.OK, because: desk.Console);
        Waybill(run).Should().Contain("OVERNIGHT",
            because: "the executor read the version the editor published, through no code this desk wrote");
    }

    [Fact]
    public async Task A_draft_is_not_what_runs_until_it_is_published()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_draft");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var created = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Two carriers",
            definitionJson = FreightDesk.AssignCarrierDefinition("SHP-9002", "Overnight", 0),
            outputParametersJson = FreightDesk.AssignCarrierOutputParametersJson,
            mode = nameof(WorkflowMode.Parallel),
            maxConcurrentRuns = 5,
        });
        var workflowId = created.Json.GetProperty("id").GetGuid();

        var drafted = await desk.EditAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new
        {
            name = "Two carriers",
            definitionJson = FreightDesk.AssignCarrierDefinition("SHP-9002", "Freight", 0),
            outputParametersJson = FreightDesk.AssignCarrierOutputParametersJson,
            autoPublish = false,
        });
        drafted.StatusCode.Should().Be(HttpStatusCode.OK, because: drafted.Body);

        Waybill(await desk.ExecuteAsync(workflowId)).Should().Contain("OVERNIGHT",
            because: "an unpublished draft is what the editor is holding, not what the engine is running");

        var published = await desk.EditAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/publish");
        published.StatusCode.Should().Be(HttpStatusCode.OK, because: published.Body);

        Waybill(await desk.ExecuteAsync(workflowId)).Should().Contain("FREIGHT");
    }

    [Fact]
    public async Task Host_metadata_written_through_the_editor_reads_back_in_the_row_and_the_list_and_clears_on_an_empty_map()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_metadata");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);
        var definition = FreightDesk.AssignCarrierDefinition("SHP-9004", "Overnight", 0);

        var created = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Filed", definitionJson = definition, outputParametersJson = FreightDesk.AssignCarrierOutputParametersJson,
            hostMetadata = new Dictionary<string, string> { ["folderId"] = "f-1", ["team"] = "north" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, because: created.Body);
        var workflowId = created.Json.GetProperty("id").GetGuid();

        async Task<(string? Row, string? Listed)> ReadAsync()
        {
            var row = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}");
            var list = await desk.EditAsync(HttpMethod.Get, "/api/workflows");
            var listed = list.Json.EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == workflowId);
            return (row.Json.GetProperty("hostMetadata").GetRawText(), listed.GetProperty("hostMetadata").GetRawText());
        }

        var stored = await ReadAsync();
        foreach (var json in new[] { stored.Row, stored.Listed })
        {
            JsonSerializer.Deserialize<Dictionary<string, string>>(json!).Should()
                .BeEquivalentTo(new Dictionary<string, string> { ["folderId"] = "f-1", ["team"] = "north" });
        }

        var cleared = await desk.EditAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new
        {
            name = "Filed", definitionJson = definition, outputParametersJson = FreightDesk.AssignCarrierOutputParametersJson,
            hostMetadata = new Dictionary<string, string>(),
        });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, because: cleared.Body);
        (await ReadAsync()).Should().Be(("null", "null"));
        (await ScalarHostMetadataIsSqlNullAsync(postgres, workflowId)).Should().BeTrue("an empty map is stored as null, not as {}");
    }

    private static async Task<bool> ScalarHostMetadataIsSqlNullAsync(string connectionString, Guid workflowId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT host_metadata IS NULL FROM automation.workflows WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", workflowId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_definition_row_written_by_something_else_is_run_by_the_executor()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_insert");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-9003", carrier: "Inserted", dwellSeconds: 0);

        var run = await desk.ExecuteAsync(workflowId);

        run.Status.Should().Be(HttpStatusCode.OK, because: desk.Console);
        Waybill(run).Should().Contain("INSERTED");

        var errors = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions?hasErrors=true");
        var all = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions");
        errors.Json.GetProperty("items").GetArrayLength().Should().Be(0, because: errors.Body);
        all.Json.GetProperty("items")[0].GetProperty("failureReason").ValueKind.Should().Be(JsonValueKind.Null, because: all.Body);
    }

    [Fact]
    public async Task A_definition_row_that_does_not_compile_is_refused_at_execute_and_its_row_says_why()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_refused");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var workflowId = await FreightDesk.SeedDefinitionAsync(postgres, "Retired block", """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"gone","blockTypeId":"dispatch.retired","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"gone","mappings":{}}]}
            """);

        var run = await desk.ExecuteAsync(workflowId);

        run.Status.Should().Be(HttpStatusCode.BadRequest, because: desk.Console);
        run.Outputs.Should().ContainKey("diagnostics");
        using var container = FreightDesk.Container(postgres);
        using var scope = container.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs
            .AsNoTracking().SingleAsync(log => log.WorkflowId == workflowId);
        (row.Status, row.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));

        var errors = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions?hasErrors=true");
        errors.StatusCode.Should().Be(HttpStatusCode.OK, because: errors.Body);
        var listed = errors.Json.GetProperty("items");
        listed.GetArrayLength().Should().Be(1, because: errors.Body);
        (listed[0].GetProperty("id").GetGuid(), listed[0].GetProperty("failureReason").GetString())
            .Should().Be((row.Id, WorkflowFailureReasons.DidNotCompile));
        var detail = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions/{row.Id}");
        detail.Json.GetProperty("failureReason").GetString().Should().Be(WorkflowFailureReasons.DidNotCompile, because: detail.Body);
        detail.Json.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_write_to_somebody_elses_workflow_is_answered_as_if_it_were_not_there()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_editor_owner");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-editor", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var deleted = await desk.EditAsync(HttpMethod.Delete, $"/api/workflows/{Guid.NewGuid()}");

        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string Waybill(DispatchResponse run) =>
        run.Outputs.TryGetValue("waybill", out var value) && value is JsonElement element
            ? element.ToString()
            : string.Join(", ", run.Outputs.Select(pair => $"{pair.Key}={pair.Value}"));

    private static DispatchReplicaSettings Settings(string id, string postgres) => new()
    {
        ReplicaId = id,
        PostgresConnectionString = postgres,
        HasPeerReplicas = false,
    };
}
