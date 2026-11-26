using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class WakePayloadOnPostgresTests(ClusterBackends backends)
{
    private static readonly TimeSpan NoPassWillCome = TimeSpan.FromMinutes(5);

    private static readonly string Payload = new('x', 10_000);

    private const int RecordedValueCap = 8 * 1024;

    [Fact]
    public async Task A_wake_carrying_ten_kilobytes_resumes_the_run_it_named_with_the_payload_intact()
    {
        await using var desk = await Desk.StartAsync(backends, "dispatch_wake_payload");

        var workflowId = await desk.SeedHoldAsync("SHP-7501", DateTimeOffset.UtcNow.AddMinutes(-1));
        await desk.RunAsync(workflowId);

        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(desk.Postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Suspended,
            TimeSpan.FromSeconds(30),
            "the hold to park");
        parked[0].BlockId.Should().Be(FreightDesk.WorkNodeId);

        var woken = await desk.WakeAsync(new WorkflowSuspensionWake
        {
            SuspensionId = parked[0].Id,
            Reason = WorkflowSuspensionWakeReasons.HelperChange,
            Data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["triggerData"] = Payload }
        });

        woken.Should().BeTrue("the wake found the row and re-entered its run, and no pass is due for five minutes");

        var resolved = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(desk.Postgres, workflowId),
            rows => rows[0].Status != WorkflowSuspensionStatus.Suspended,
            TimeSpan.FromSeconds(30),
            "the run the wake re-entered");

        resolved[0].Status.Should().Be(WorkflowSuspensionStatus.Resumed,
            "the wake answered the wait, which is not the same as the deadline running out");
        using var outcome = JsonDocument.Parse(resolved[0].OutcomeJson!);
        outcome.RootElement.GetProperty("triggerData").GetString().Should().Be(Payload,
            "the take stored what the host sent, whole: the value this release widened the column for");
        outcome.RootElement.GetProperty(WorkflowResumeDecisions.ResumeReasonKey).GetString()
            .Should().Be(WorkflowSuspensionWakeReasons.HelperChange,
                "the row was answered with the wake's own reason, where a pass writes 'poll'");

        var runs = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(desk.Postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == nameof(WorkflowRunStateStatus.Completed),
            TimeSpan.FromSeconds(30),
            "the resumed run to finish");

        runs[0].FailureReason.Should().BeNull();

        using var recorded = JsonDocument.Parse(await desk.RunOutputsAsync(workflowId));
        recorded.RootElement.GetProperty("payload").GetString().Should().Be(
            Payload[..RecordedValueCap] + "...[truncated]",
            "the run carried the payload's own characters up to the cap, and the note the option promises past it");

        recorded.RootElement.GetProperty("size").GetInt32().Should().Be(
            Payload.Length,
            "the block after the wait was handed every character in the resumed string value");
        recorded.RootElement.GetProperty("outcome").GetString().Should().Be(
            WorkflowWaitOutcomes.Satisfied,
            "and the branch the count was taken on is the wake's answer, not the hold's own backstop timing out");
    }

    private sealed class Desk : IAsyncDisposable
    {
        private readonly WebApplication app;

        private Desk(string postgres, WebApplication app)
        {
            Postgres = postgres;
            this.app = app;
        }

        public string Postgres { get; }

        public static async Task<Desk> StartAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            var app = DispatchHost.Build(new DispatchHostOptions
            {
                PostgresConnectionString = postgres,
                RedisConnectionString = backends.RedisConnectionString,
                ReplicaId = "desk-wake-payload",
                HasPeerReplicas = true,
                MigrateOnStartup = false,
                WakerPollInterval = NoPassWillCome
            });

            await using (var scope = app.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();

            return new Desk(postgres, app);
        }

        public Task<Guid> SeedHoldAsync(string shipment, DateTimeOffset untilUtc) =>
            FreightDesk.SeedDefinitionAsync(
                Postgres, WorkflowMode.Parallel, $"Hold {shipment}",
                $$"""
                {
                  "nodes": [
                    { "id": "start", "blockTypeId": "WorkflowInput", "position": { "x": 0, "y": 0 },"configuration":{} },
                    {
                      "id": "{{FreightDesk.WorkNodeId}}",
                      "blockTypeId": "dispatch.hold-until",
                      "position": { "x": 240, "y": 0 },
                      "configuration": {
                        "shipment": "{{shipment}}",
                        "untilUtc": "{{untilUtc.ToString("O", CultureInfo.InvariantCulture)}}",
                        "released": "{{FreightDesk.ReleasedWhen(untilUtc)}}"
                      }
                    },
                    { "id": "text", "blockTypeId": "Expression", "position": { "x": 360, "y": 0 }, "configuration": {} },
                    {
                      "id": "size",
                      "blockTypeId": "Expression",
                      "position": { "x": 480, "y": 0 },
                      "configuration": { "Expression": "expr:len(${previous.text.Result})" }
                    },
                    { "id": "end", "blockTypeId": "WorkflowOutput", "position": { "x": 720, "y": 0 }, "configuration": {} }
                  ],
                  "edges": [
                    { "id": "start-to-work", "sourceNodeId": "start", "targetNodeId": "{{FreightDesk.WorkNodeId}}","mappings":{} },
                    {
                      "id": "work-to-text", "sourceNodeId": "{{FreightDesk.WorkNodeId}}", "targetNodeId": "text",
                      "mappings": {
                        "Expression": { "type": "direct", "sourceOutputName": "triggerData", "projection": { "path": "$", "type": "String" } }
                      }
                    },
                    { "id": "text-to-size", "sourceNodeId": "text", "targetNodeId": "size","mappings":{} },
                    {
                      "id": "work-to-end", "sourceNodeId": "{{FreightDesk.WorkNodeId}}", "targetNodeId": "end",
                      "mappings": {
                        "outcome": { "type": "direct", "sourceOutputName": "outcome" },
                        "payload": { "type": "direct", "sourceOutputName": "triggerData", "projection": { "path": "$" } }
                      }
                    },
                    {
                      "id": "size-to-end", "sourceNodeId": "size", "targetNodeId": "end",
                      "mappings": { "size": { "type": "direct", "sourceOutputName": "Result" } }
                    }
                  ]
                }
                """,
                """[{ "name": "outcome", "type": "String", "isRequired": false }, { "name": "payload", "type": "String", "isRequired": false }, { "name": "size", "type": "Number", "isRequired": false }]""");

        public async Task RunAsync(Guid workflowId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteAsync(workflowId, null, null);
        }

        public async Task<bool> WakeAsync(WorkflowSuspensionWake wake)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<WorkflowSuspensionWaker>()
                .WakeNowAsync(wake, CancellationToken.None);
        }

        public async Task<string> RunOutputsAsync(Guid workflowId)
        {
            await using var connection = new NpgsqlConnection(Postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT outputs_json::text FROM automation.workflow_execution_logs WHERE workflow_id = @id",
                connection);
            command.Parameters.AddWithValue("id", workflowId);
            return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
