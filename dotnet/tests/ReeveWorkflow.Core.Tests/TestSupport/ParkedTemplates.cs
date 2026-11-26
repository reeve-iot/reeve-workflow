using System.Collections.Concurrent;
using System.Text.Json;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Execution;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal static class ParkedTemplates
{
    public const string BlockType = "test.template-wait";

    public const string ThresholdParameter = """[{"name":"threshold","type":"Int","isRequired":true}]""";

    private static readonly ConcurrentDictionary<Guid, WorkflowExecutionLog> Logs = new();
    private static readonly ConcurrentDictionary<Guid, WorkflowVersion> Versions = new();
    private static readonly ConcurrentDictionary<Guid, int> VersionLoads = new();

    private static readonly IWorkflowBlocks Blocks = new UncheckedWorkflowBlocks(
        [new LambdaFactory(BlockType, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort])]);

    public static WorkflowSuspensionCandidate Pin(
        WorkflowSuspensionCandidate row, string condition, string inputParametersJson = ThresholdParameter)
    {
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new
                {
                    id = row.BlockId, blockTypeId = BlockType, position = new { x = 0, y = 0 },
                    configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:" + condition }
                }
            },
            edges = new object[] { new { id = "in", sourceNodeId = "start", targetNodeId = row.BlockId, mappings = new { } } }
        });
        var version = new WorkflowVersion(Guid.NewGuid(), row.WorkflowId, 1, definition, inputParametersJson, null,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Versions[version.Id] = version;
        Logs[row.ExecutionLogId] = new WorkflowExecutionLog(row.ExecutionLogId, null, row.WorkflowId, version.Id, null,
            WorkflowExecutionStatus.Running, DateTimeOffset.UnixEpoch);
        return row with
        {
            ConfigJson = JsonSerializer.Serialize(new WorkflowStoredTemplateConfig(TemplateWaits.Input, null, condition))
        };
    }

    public static WorkflowSuspensionCandidate PinToSameVersion(
        WorkflowSuspensionCandidate row, string condition, Guid sourceExecutionLogId)
    {
        var source = Logs[sourceExecutionLogId];
        Logs[row.ExecutionLogId] = new WorkflowExecutionLog(row.ExecutionLogId, null, row.WorkflowId, source.WorkflowVersionId,
            null, WorkflowExecutionStatus.Running, DateTimeOffset.UnixEpoch);
        return row with
        {
            ConfigJson = JsonSerializer.Serialize(new WorkflowStoredTemplateConfig(TemplateWaits.Input, null, condition))
        };
    }

    public static int VersionLoadCount(Guid versionId) => VersionLoads.GetValueOrDefault(versionId);

    public static Guid VersionIdFor(Guid executionLogId) => Logs[executionLogId].WorkflowVersionId;

    public static void ForgetVersion(Guid versionId) => Versions.TryRemove(versionId, out _);

    public static WorkflowCompilationService Compiler(IEnumerable<WorkflowExpressionNamespace>? namespaces = null, IHelperResolver? helper = null) =>
        new(Blocks, Scopes(), new CompilationOptions { NamespaceRegistrations = [.. namespaces ?? []], HelperResolver = helper ?? NullHelperResolver.Instance });

    public static IWorkflowRunScopeFactory Scopes(IWorkflowRunScopeFactory? inner = null) => new PinnedScopes(inner);

    private sealed class PinnedScopes(IWorkflowRunScopeFactory? inner) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope() => new Scope(inner?.CreateScope());

        private sealed class Scope(IWorkflowRunScope? inner) : IWorkflowRunScope, IServiceProvider
        {
            public IServiceProvider Services => this;
            public IWorkflowStore Store { get; } = new PinnedStore();
            public IWorkflowRunJournal Journal { get; } = new PinnedJournal(inner?.Journal);
            public IWorkflowAuditSink Audit => inner?.Audit ?? throw new NotSupportedException();
            public IWorkflowAuthorization Authorization => inner?.Authorization ?? throw new NotSupportedException();
            public object? GetService(Type serviceType) => null;
            public void Dispose() => inner?.Dispose();
        }
    }

    private sealed class PinnedJournal(IWorkflowRunJournal? inner) : IWorkflowRunJournal
    {
        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken) =>
            inner?.OpenRunAsync(log, cancellationToken) ?? Task.CompletedTask;

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Logs.TryGetValue(executionLogId, out var log) ? Task.FromResult<WorkflowExecutionLog?>(log)
            : inner?.LoadRunAsync(executionLogId, cancellationToken) ?? Task.FromResult<WorkflowExecutionLog?>(null);

        public Task SaveRunDetailsAsync(Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken) =>
            inner?.SaveRunDetailsAsync(executionId, recordLogEntries, logEntries, executionSteps, cancellationToken) ?? Task.CompletedTask;

        public Task SaveRunAsync(CancellationToken cancellationToken) => inner?.SaveRunAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class PinnedStore : IWorkflowStore
    {
        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<Workflow?>(null);

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult(Load(versionId));

        private static WorkflowVersion? Load(Guid versionId)
        {
            VersionLoads.AddOrUpdate(versionId, 1, static (_, count) => count + 1);
            return Versions.TryGetValue(versionId, out var version) ? version : null;
        }

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid VersionId, string DefinitionJson)?>(null);
    }
}
