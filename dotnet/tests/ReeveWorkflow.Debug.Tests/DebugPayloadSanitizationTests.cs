using System.Text.Json.Nodes;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public sealed class DebugPayloadSanitizationTests
{
    [Fact]
    public async Task Node_completion_notifier_masks_nested_values_and_preserves_siblings()
    {
        var notifier = new SnapshotNotifier();
        var hook = new WorkflowDebugHook(
            Guid.NewGuid(), Guid.NewGuid(), ["node"], null!, notifier,
            NullWorkflowMetrics.Instance, NullLogger.Instance);
        var output = new Dictionary<string, object?>
        {
            ["items"] = new object?[]
            {
                new Dictionary<string, object?> { ["credential"] = "hidden", ["name"] = "visible" }
            }
        };

        await hook.OnNodeCompletedAsync("node", output, CancellationToken.None);

        Assert.Contains("\"credential\":\"***\"", notifier.Payload);
        Assert.Contains("\"name\":\"visible\"", notifier.Payload);
    }

    [Fact]
    public async Task Persisted_debug_snapshot_masks_nested_inputs_and_variables()
    {
        var notifier = new SnapshotNotifier();
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connection));
        });
        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<WorkflowDbContext>();
        await db.Database.EnsureCreatedAsync();
        var owner = Guid.NewGuid();
        var workflow = new Workflow(Guid.NewGuid(), "snapshot", null, owner, DateTimeOffset.UtcNow);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var notifierLog = new RecordingSnapshotNotifier();
        var blobs = new SnapshotBlobs();
        using var service = new WorkflowDebugSessionService(
            provider.GetRequiredService<IServiceScopeFactory>(), notifierLog, NullWorkflowMetrics.Instance,
            new LoggingWorkflowAuditSink(NullLogger<LoggingWorkflowAuditSink>.Instance), blobs,
            Options.Create(new WorkflowDebugOptions()), NullLogger<WorkflowDebugSessionService>.Instance);
        var session = await service.CreateAsync(workflow.Id, owner);
        await service.SetBreakpointsAsync(session.Id, ["node"]);
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, "{}", null, null, owner, DateTimeOffset.UtcNow);
        db.WorkflowVersions.Add(version);
        var execution = new WorkflowExecutionLog(Guid.NewGuid(), null, workflow.Id, version.Id, null,
            WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        db.WorkflowExecutionLogs.Add(execution);
        await db.SaveChangesAsync();
        await service.BindToExecutionAsync(session.Id, execution.Id);
        var hook = service.GetHookForExecution(execution.Id)!;
        var inputs = new Dictionary<string, object?>
        {
            ["items"] = new object?[] { new Dictionary<string, object?> { ["secret"] = "hidden", ["label"] = "visible" } }
        };
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["getToken"] = new Dictionary<string, object?>
            {
                ["api_key"] = "hidden", ["name"] = "visible", ["longValue"] = new string('x', 70_000)
            }
        };

        var paused = hook.OnNodeAsync("node", inputs, outputs, CancellationToken.None);
        await notifierLog.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.ResumeAsync(session.Id);
        await paused.WaitAsync(TimeSpan.FromSeconds(5));

        using var pausedJson = JsonDocument.Parse(notifierLog.PausedPayload);
        var snapshotId = pausedJson.RootElement.GetProperty("Id").GetGuid();
        var blobBytes = await blobs.GetBytesAsync($"{session.Id}/snapshots/{snapshotId}");
        blobBytes.Should().NotBeNull("the hook persists the same sanitized snapshot it sends to the notifier");
        using var snapshotBlob = JsonDocument.Parse(blobBytes!);
        var storedInputs = JsonNode.Parse(Find(snapshotBlob.RootElement, "inputsJson").GetString()!)!;
        storedInputs["items"]![0]!["secret"]!.GetValue<string>().Should().Be("***");
        storedInputs["items"]![0]!["label"]!.GetValue<string>().Should().Be("visible");
        var storedVariables = JsonNode.Parse(Find(snapshotBlob.RootElement, "variablesJson").GetString()!)!;
        storedVariables["getToken"]!["api_key"]!.GetValue<string>().Should().Be("***");
        storedVariables["getToken"]!["name"]!.GetValue<string>().Should().Be("visible");
        storedVariables["getToken"]!["longValue"]!.GetValue<string>().Should().StartWith("xxxxxxxx");
        storedVariables["getToken"]!["longValue"]!.GetValue<string>().Should().EndWith("...[truncated]");
        var notifiedInputs = JsonNode.Parse(Find(pausedJson.RootElement, "inputsJson").GetString()!)!;
        notifiedInputs["items"]![0]!["secret"]!.GetValue<string>().Should().Be("***");
        var notifiedVariables = JsonNode.Parse(Find(pausedJson.RootElement, "variablesJson").GetString()!)!;
        notifiedVariables["getToken"]!["api_key"]!.GetValue<string>().Should().Be("***");
    }

    private static JsonElement Find(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class SnapshotNotifier : IWorkflowDebugNotifier
    {
        public string Payload { get; private set; } = "";

        public Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        {
            Payload = snapshotJson;
            return Task.CompletedTask;
        }
        public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingSnapshotNotifier : IWorkflowDebugNotifier
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PausedPayload { get; private set; } = "";
        public Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        {
            PausedPayload = snapshotJson;
            Paused.TrySetResult();
            return Task.CompletedTask;
        }
        public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SnapshotBlobs : IWorkflowDebugBlobStore
    {
        private readonly Dictionary<string, byte[]> _items = [];
        public Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
        {
            _items[key] = bytes;
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.GetValueOrDefault(key));
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }
    }
}
