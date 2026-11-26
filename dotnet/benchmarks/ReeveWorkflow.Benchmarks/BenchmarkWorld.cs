using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Benchmarks;

internal sealed class BenchmarkWorld
{
    private const string Leaf = "Leaf";
    public const string Relay = "Relay";
    public const string Echo = "Echo";
    public const string Source = "Source";
    public const string Reading = "Reading";
    public const string Decision = "Decision";
    public const string TemplateWait = "TemplateWait";
    public const string ConditionInput = "Condition";
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentDictionary<string, IWorkflowBlockFactory> catalogue = new();

    public BenchmarkWorld()
    {
        catalogue[Leaf] = new Factory(Leaf, holds: false);
        catalogue[Relay] = new RelayFactory(this);
        catalogue[Echo] = new EchoFactory(this);
        catalogue["IterateOver"] = new IterateOverBlockFactory();
        catalogue[Source] = new Factory(Source, holds: false, outputs:
        [
            new("x", "x", PortValueType.Number, false), new("y", "y", PortValueType.Int, false),
            new("z", "z", PortValueType.Number, false), new("s", "s", PortValueType.String, false),
            new("ok", "ok", PortValueType.Boolean, false),
        ]);
        catalogue[Reading] = new Factory(Reading, holds: false, outputs:
            [new("temperature", "temperature", PortValueType.Number, false), new("unit", "unit", PortValueType.String, false)]);
        catalogue[Decision] = new Factory(Decision, holds: false, outputs:
            [new("heat", "heat", PortValueType.Boolean, false), new("reason", "reason", PortValueType.String, false)]);
        catalogue[TemplateWait] = new Factory(TemplateWait, holds: false,
            inputs: [new(ConditionInput, ConditionInput, PortValueType.OfDeclaration("Expression<Boolean>"), false)]);
        Blocks = new Catalogue(this);
        Scopes = new ScopeFactory(this);
        Journal = new GateJournal();
    }

    public Action<string?, IReadOnlyDictionary<string, object?>>? Probe { get; set; }

    public Guid Owner { get; } = Guid.NewGuid();
    public GateStore Store { get; } = new();
    public GateJournal Journal { get; }
    public IWorkflowBlocks Blocks { get; }
    public ScopeFactory Scopes { get; }

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
        CompilationOptions? options = null, IWorkflowRunStateStore? runState = null, WorkflowCompilationService? compiler = null) =>
        new(
            new WorkflowEngineSeams
            {
                RunScopes = Scopes,
                Blocks = Blocks,
                Compiler = compiler ?? Compiler(options),
                ExecutionGate = AlwaysRunningExecutionGate.Instance,
                Metrics = NullWorkflowMetrics.Instance,
                NodeObserver = NullWorkflowNodeObserver.Instance,
                RunStateStore = runState
            },
            new WorkflowEngineSettings { ReplicaId = "benchmark" },
            NullLoggerFactory.Instance);

    private sealed class Catalogue(BenchmarkWorld world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => world.catalogue.GetValueOrDefault(blockTypeId);
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [.. world.catalogue.Values];
        public string Generation => "1";
    }

    internal sealed class ScopeFactory(BenchmarkWorld world) : IWorkflowRunScopeFactory
    {
        private int opened;
        public int Opened => Volatile.Read(ref opened);

        public IWorkflowRunScope CreateScope()
        {
            Interlocked.Increment(ref opened);
            return new Scope(world);
        }

        private sealed class Scope(BenchmarkWorld world) : IWorkflowRunScope, IServiceProvider
        {
            public IServiceProvider Services => this;
            public IWorkflowStore Store => world.Store;
            public IWorkflowRunJournal Journal => world.Journal;
            public IWorkflowAuditSink Audit { get; } = new NoAudit();
            public IWorkflowAuthorization Authorization { get; } = new Everyone();

            public object? GetService(Type serviceType) => null;
            public void Dispose() { }
        }
    }

    private sealed class Factory(
        string id, bool holds, BlockPortDefinition[]? inputs = null, BlockPortDefinition[]? outputs = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "benchmark";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public IWorkflowBlockPlugin Create() => new Block(holds);
    }

    internal sealed class RelayFactory(BenchmarkWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Relay;
        public string DisplayName => Relay;
        public string Description => string.Empty;
        public string Category => "benchmark";

        public IReadOnlyCollection<BlockPortDefinition> Inputs { get; } =
        [
            new("A", "A", PortValueType.Number, true),
            new("B", "B", PortValueType.Number, true),
            new("C", "C", PortValueType.Number, true),
            new("Calc", "Calc", PortValueType.Number, true),
            new("Label", "Label", PortValueType.String, false),
        ];

        public IReadOnlyCollection<BlockPortDefinition> Outputs { get; } =
        [
            new("a", "a", PortValueType.Number, false),
            new("b", "b", PortValueType.Number, false),
            new("c", "c", PortValueType.Number, false),
            new("label", "label", PortValueType.String, false),
        ];

        public IWorkflowBlockPlugin Create() => new RelayBlock(world);
    }

    private sealed class RelayBlock(BenchmarkWorld world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.Probe?.Invoke(context.NodeId, inputs);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["a"] = Convert.ToDouble(inputs["A"]) + 1,
                ["b"] = Convert.ToDouble(inputs["B"]) + 1,
                ["c"] = Convert.ToDouble(inputs["C"]) + Convert.ToDouble(inputs["Calc"]),
                ["label"] = inputs.GetValueOrDefault("Label"),
            });
        }
    }

    internal sealed class EchoFactory(BenchmarkWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Echo;
        public string DisplayName => Echo;
        public string Description => string.Empty;
        public string Category => "benchmark";
        public IReadOnlyCollection<BlockPortDefinition> Inputs { get; } = [new("V", "V", PortValueType.Number, false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs { get; } = [new("R", "R", PortValueType.Number, false)];
        public IWorkflowBlockPlugin Create() => new EchoBlock(world);
    }

    private sealed class EchoBlock(BenchmarkWorld world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.Probe?.Invoke(context.NodeId, inputs);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["R"] = inputs.GetValueOrDefault("V") });
        }
    }

    private sealed class Block(bool holds) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (holds)
                await Task.Delay(1000, cancellationToken);
            return new Dictionary<string, object?>();
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

    internal sealed class GateJournal : IWorkflowRunJournal
    {
        public ConcurrentDictionary<Guid, WorkflowExecutionLog> Logs { get; } = new();

        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            Logs[log.Id] = log;
            return Task.CompletedTask;
        }

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Task.FromResult(Logs.GetValueOrDefault(executionLogId));

        public Task SaveRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    internal sealed class GateStore : IWorkflowStore
    {
        public ConcurrentDictionary<Guid, Workflow> Workflows { get; } = new();
        public ConcurrentDictionary<Guid, WorkflowVersion> Versions { get; } = new();

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(Workflows.GetValueOrDefault(workflowId));

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult(Versions.GetValueOrDefault(versionId));

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(Workflows.GetValueOrDefault(workflowId)?.CurrentVersion is { } current
                ? ((Guid, string)?)(current.Id, current.DefinitionJson)
                : null);

        public Task<IReadOnlyList<WorkflowVersion>> LoadVersionsAsync(
            Guid workflowId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowVersion>>(
                Versions.Values.Where(v => v.WorkflowId == workflowId).Skip(skip).Take(take).ToList());
    }
}
