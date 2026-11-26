using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

internal sealed class AdmissionGateWorld
{
    public const string Leaf = "Leaf";
    public const string Hold = "Hold";
    public const string Resolved = "Resolved";
    public const string Sink = "Sink";
    public const string Call = WorkflowGraphHelpers.SubWorkflowBlockTypeId;

    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentDictionary<string, IWorkflowBlockFactory> catalogue = new(StringComparer.OrdinalIgnoreCase);
    private int generation;

    public AdmissionGateWorld()
    {
        foreach (var factory in new IWorkflowBlockFactory[]
                 {
                     new Factory(Leaf, this, holds: false), new Factory(Hold, this, holds: true),
                     new CallFactory(this), new ResolvedFactory(this), new SinkFactory(this)
                 })
            catalogue[factory.BlockTypeId] = factory;
        Blocks = new Catalogue(this);
        Scopes = new ScopeFactory(this);
    }

    public List<BlockPortDefinition> SinkInputs { get; } = [];

    public Guid Owner { get; } = Guid.NewGuid();
    public GateStore Store { get; } = new();
    public GateJournal Journal { get; } = new();
    public IWorkflowBlocks Blocks { get; }
    public ScopeFactory Scopes { get; }

    public ConcurrentQueue<string> Executed { get; } = new();

    public ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>> Received { get; } = new();

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<INodePortResolver> Resolvers { get; } = [];

    public void Remove(string blockTypeId)
    {
        catalogue.TryRemove(blockTypeId, out _);
        Interlocked.Increment(ref generation);
    }

    public Guid AddWorkflow(string definitionJson, WorkflowMode mode = WorkflowMode.Parallel,
        string? inputParametersJson = null, string? outputParametersJson = null)
    {
        var id = Guid.NewGuid();
        var workflow = new Workflow(id, "wf", null, Owner, T0);
        workflow.ConfigureConcurrency(mode, 4, 10, WorkflowOverflowBehavior.Drop, T0);
        Store.Workflows[id] = workflow;
        Publish(id, definitionJson, inputParametersJson, outputParametersJson);
        return id;
    }

    public WorkflowVersion Publish(
        Guid workflowId, string definitionJson, string? inputParametersJson = null, string? outputParametersJson = null)
    {
        var workflow = Store.Workflows[workflowId];
        var number = Store.Versions.Values.Count(v => v.WorkflowId == workflowId) + 1;
        var version = new WorkflowVersion(Guid.NewGuid(), workflowId, number, definitionJson,
            inputParametersJson, outputParametersJson, Owner, T0, isPublished: true);
        Store.Versions[version.Id] = version;
        workflow.SetCurrentVersion(version, T0);
        return version;
    }

    public WorkflowCompilationService Compiler(CompilationOptions? options = null) =>
        new(Blocks, Scopes, options);

    public WorkflowEngine Engine(
        CompilationOptions? options = null, IWorkflowRunStateStore? runState = null, WorkflowCompilationService? compiler = null,
        IWorkflowStartAdmission? startAdmission = null, IReadOnlyList<WorkflowExpressionNamespace>? hostNamespaces = null,
        IWorkflowNodeValidator? nodeValidator = null, IWorkflowStartArgumentValidator? argumentValidator = null,
        IHelperResolver? helperResolver = null) =>
        new(
            new WorkflowEngineSeams
            {
                RunScopes = Scopes,
                Blocks = Blocks,
                Compiler = compiler ?? Compiler(options),
                ExecutionGate = AlwaysRunningExecutionGate.Instance,
                Metrics = NullWorkflowMetrics.Instance,
                NodeObserver = NullWorkflowNodeObserver.Instance,
                RunStateStore = runState,
                StartAdmission = startAdmission,
                NodeValidator = nodeValidator,
                HelperResolver = helperResolver,
                StartArgumentValidator = argumentValidator
            },
            new WorkflowEngineSettings { ReplicaId = "replica-1", ExpressionNamespaces = hostNamespaces ?? [] },
            NullLoggerFactory.Instance);

    public static string Graph(params (string Id, string Block, object? Configuration)[] nodes)
    {
        var all = new List<object> { new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } } };
        var edges = new List<object>();
        var previous = "start";
        foreach (var (id, block, configuration) in nodes)
        {
            all.Add(new { id, blockTypeId = block, position = new { x = 0, y = 0 }, configuration = configuration ?? new { } });
            edges.Add(new { id = $"{previous}-{id}", sourceNodeId = previous, targetNodeId = id, mappings = new { } });
            previous = id;
        }

        return JsonSerializer.Serialize(new { nodes = all, edges });
    }

    public static object Calls(Guid child) => new { WorkflowId = child.ToString() };

    private sealed class Catalogue(AdmissionGateWorld world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => world.catalogue.GetValueOrDefault(blockTypeId);

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [.. world.catalogue.Values];

        public string Generation => Volatile.Read(ref world.generation).ToString();
    }

    private class Factory(string id, AdmissionGateWorld world, bool holds) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public virtual IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new Block(world, holds);
    }

    private sealed class CallFactory(AdmissionGateWorld world) : Factory(Call, world, holds: false), IWorkflowBlockFactory
    {
        public override IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("WorkflowId", "Workflow", PortValueType.String, true)];
        public bool PortsAreResolvedElsewhere => true;
        public string? RunsWorkflowFrom => "WorkflowId";
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
            [new BlockConfigFieldDefinition("WorkflowId", "Workflow", ConfigFieldType.String, Required: false)];
    }

    private sealed class SinkFactory(AdmissionGateWorld world) : Factory(Sink, world, holds: false)
    {
        public override IReadOnlyCollection<BlockPortDefinition> Inputs => world.SinkInputs;
    }

    private sealed class ResolvedFactory(AdmissionGateWorld world) : Factory(Resolved, world, holds: false), IWorkflowBlockFactory
    {
        public bool PortsAreResolvedElsewhere => true;
    }

    private sealed class Block(AdmissionGateWorld world, bool holds) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.Executed.Enqueue(context.NodeId ?? string.Empty);
            world.Received[context.NodeId ?? string.Empty] = inputs;
            if (holds)
            {
                world.Entered.TrySetResult();
                await world.Release.Task.WaitAsync(cancellationToken);
            }

            return new Dictionary<string, object?>();
        }
    }

    internal sealed class GateStore : IWorkflowStore
    {
        public ConcurrentDictionary<Guid, Workflow> Workflows { get; } = new();
        public ConcurrentDictionary<Guid, WorkflowVersion> Versions { get; } = new();
        public ConcurrentQueue<string> Reads { get; } = new();

        public Guid? FailVersionLoad { get; set; }

        public Exception? FailWith { get; set; }

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            Reads.Enqueue($"workflow {workflowId}");
            return Task.FromResult(Workflows.GetValueOrDefault(workflowId));
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken)
        {
            Reads.Enqueue($"version {versionId}");
            if (versionId == FailVersionLoad)
                return Task.FromException<WorkflowVersion?>(FailWith ?? new TimeoutException("The store did not answer."));
            return Task.FromResult(Versions.GetValueOrDefault(versionId));
        }

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken)
        {
            Reads.Enqueue($"current {workflowId}");
            return Task.FromResult(Workflows.GetValueOrDefault(workflowId)?.CurrentVersion is { } current
                ? ((Guid, string)?)(current.Id, current.DefinitionJson)
                : null);
        }
    }

    internal sealed class GateJournal : IWorkflowRunJournal
    {
        public ConcurrentDictionary<Guid, WorkflowExecutionLog> Logs { get; } = new();
        public ConcurrentDictionary<Guid, IReadOnlyList<WorkflowExecutionLogEntry>> Entries { get; } = new();

        public bool OpenFails { get; set; }

        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            if (OpenFails)
                return Task.FromException(new TimeoutException("The journal did not answer."));
            Logs[log.Id] = log;
            return Task.CompletedTask;
        }

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Task.FromResult(Logs.GetValueOrDefault(executionLogId));

        public Task SaveRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken)
        {
            if (executionId is { } id && recordLogEntries)
                Entries[id] = [.. logEntries];
            return Task.CompletedTask;
        }
    }

    internal sealed class ScopeFactory(AdmissionGateWorld world) : IWorkflowRunScopeFactory
    {
        private int opened;

        public int Opened => Volatile.Read(ref opened);

        public IWorkflowRunScope CreateScope()
        {
            Interlocked.Increment(ref opened);
            return new Scope(world);
        }

        private sealed class Scope(AdmissionGateWorld world) : IWorkflowRunScope, IServiceProvider
        {
            public IServiceProvider Services => this;
            public IWorkflowStore Store => world.Store;
            public IWorkflowRunJournal Journal => world.Journal;
            public IWorkflowAuditSink Audit { get; } = new NoAudit();
            public IWorkflowAuthorization Authorization { get; } = new Everyone();

            public object? GetService(Type serviceType) =>
                serviceType == typeof(IEnumerable<INodePortResolver>) ? world.Resolvers.ToArray() : null;

            public void Dispose() { }
        }
    }

    private sealed class NoAudit : IWorkflowAuditSink
    {
        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Everyone : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
