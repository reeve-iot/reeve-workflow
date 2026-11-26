using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowEngineRunRegistrationTests : IDisposable
{
    private const string Failing = "test.failing";
    private const string Reading = "test.reading";

    private readonly SqliteConnection connection = new("Filename=:memory:");

    public WorkflowEngineRunRegistrationTests() => connection.Open();

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task The_run_follows_the_rule_the_call_that_composed_the_engine_stated(bool retryOnlyIdempotent, int executions)
    {
        var blocks = new CountingBlocks();
        using var provider = Build(blocks, engine: options => options.RetryOnlyIdempotentBlocks = retryOnlyIdempotent);
        var workflowId = SeedFailingWorkflow(provider);

        var run = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        await run.Should().ThrowAsync<Exception>("the block fails on every attempt");
        blocks.Executions.Should().Be(executions);
    }

    [Fact]
    public async Task A_host_that_says_nothing_gets_the_rule_this_release_ships()
    {
        var blocks = new CountingBlocks();
        using var provider = Build(blocks);
        var workflowId = SeedFailingWorkflow(provider);

        var run = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        await run.Should().ThrowAsync<Exception>();
        blocks.Executions.Should().Be(1, "the block declares nothing, and silence now means 'do not repeat'");
    }

    [Fact]
    public async Task A_walk_options_object_a_host_registered_for_its_own_reasons_is_not_the_engines()
    {
        var blocks = new CountingBlocks();
        using var provider = Build(
            blocks,
            after: services => services.AddSingleton(
                new WorkflowGraphExecutorOptions { RetryOnlyIdempotentBlocks = false }));
        var workflowId = SeedFailingWorkflow(provider);

        var run = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        await run.Should().ThrowAsync<Exception>();
        blocks.Executions.Should().Be(1,
            "the deployment's answer is the one it wrote in the call; a host's own object is not a seam");
    }

    [Fact]
    public async Task The_run_follows_the_call_that_composed_the_engine_not_a_stray_options_registration()
    {
        var blocks = new CountingBlocks();
        using var provider = Build(
            blocks,
            engine: options => options.RetryOnlyIdempotentBlocks = false,
            after: services => services.AddSingleton(new WorkflowEngineOptions()));
        var workflowId = SeedFailingWorkflow(provider);

        var run = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        await run.Should().ThrowAsync<Exception>();
        blocks.Executions.Should().Be(2, "the deployment asked for the migration escape hatch in that call");
    }

    [Fact]
    public async Task A_restart_run_is_ordered_in_the_workflow_tables_by_the_call_that_composed_the_engine()
    {
        using var provider = Build(new CountingBlocks());
        var workflowId = SeedFailingWorkflow(provider, WorkflowMode.Restart);

        var run = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);
        await run.Should().ThrowAsync<Exception>();

        provider.GetRequiredService<IWorkflowRestartGenerations>().Should().BeOfType<EfWorkflowRestartGenerations>();
        using var scope = provider.CreateScope();
        var generations = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowRestartGenerations
            .Single(row => row.WorkflowId == workflowId);
        generations.Issued.Should().Be(1);
        generations.Started.Should().Be(1);
    }

    [Fact]
    public async Task The_namespace_a_host_registers_answers_inside_the_run_that_registration_composed()
    {
        var blocks = new ReadingBlocks();
        using var provider = Build(blocks, defaults: host => host.ExpressionNamespaces.Add(
            new WorkflowExpressionNamespace("depot", 1, new DepotCheck(),
                (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new DepotNamespace()))));
        var workflowId = SeedReadingWorkflow(provider);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        blocks.Seen.Should().Equal(["granted"],
            "the run reads the vocabulary the host registered, not an empty copy of it");
    }

    [Fact]
    public async Task A_host_that_registers_no_namespaces_cannot_run_a_workflow_that_reads_one()
    {
        var blocks = new ReadingBlocks();
        using var provider = Build(blocks);
        var workflowId = SeedReadingWorkflow(provider);

        var act = () => provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        var refusal = await act.Should().ThrowAsync<WorkflowDoesNotCompileException>();
        refusal.Which.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
        blocks.Seen.Should().BeEmpty("an unknown namespace is refused before execution");
    }

    private ServiceProvider Build(
        IWorkflowBlocks blocks,
        Action<WorkflowEngineOptions>? engine = null,
        Action<IServiceCollection>? after = null,
        Action<WorkflowHostDefaultsOptions>? defaults = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(blocks);

        var builder = services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(connection));
            defaults?.Invoke(options.Defaults);
        });
        builder.AddWorkflowEngine(engine);
        after?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static Guid SeedFailingWorkflow(IServiceProvider provider, WorkflowMode mode = WorkflowMode.Single)
    {
        const string definitionJson = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"work","blockTypeId":"test.failing",
                       "configuration":{"retryCount":1,"retryBackoffSeconds":0},"position":{"x":0,"y":0}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"work","mappings":{}}]}
            """;

        return Seed(provider, definitionJson, mode);
    }

    private static Guid Seed(
        IServiceProvider provider, string definitionJson, WorkflowMode mode = WorkflowMode.Single)
    {
        var workflowId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(workflowId, "wf", null, ownerId, now);
        workflow.ConfigureConcurrency(mode, 1, 0, WorkflowOverflowBehavior.Drop, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();

        var version = new WorkflowVersion(
            Guid.NewGuid(), workflowId, 1, definitionJson, null, null, ownerId, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();

        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return workflowId;
    }

    private static Guid SeedReadingWorkflow(IServiceProvider provider)
    {
        const string definitionJson = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"read","blockTypeId":"test.reading",
                       "configuration":{"reading":"expr:${depot.clearance}"},"position":{"x":0,"y":0}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"read","mappings":{}}]}
            """;

        return Seed(provider, definitionJson);
    }

    public void Dispose() => connection.Dispose();

    private sealed class DepotNamespace : IExpressionNamespaceResolver
    {
        public string Namespace => "depot";

        public ResolveResult Resolve(string[] segments) =>
            segments is ["clearance"]
                ? ResolveResult.Resolved("granted")
                : ResolveResult.Failed();
    }

    private sealed class DepotCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "depot";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }

    private sealed class ReadingBlocks : IWorkflowBlocks
    {
        public List<object?> Seen { get; } = [];

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == Reading ? new ReadingFactory(Seen) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new ReadingFactory(Seen)];
    }

    private sealed class ReadingFactory(List<object?> seen) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Reading;
        public string DisplayName => "Reading";
        public string Description => "Records what the walk resolved for one expression.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new ReadingBlock(seen);
    }

    private sealed class ReadingBlock(List<object?> seen) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
            CancellationToken cancellationToken)
        {
            seen.Add(inputs.TryGetValue("reading", out var reading) ? reading : "<absent>");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class CountingBlocks : IWorkflowBlocks
    {
        private int executions;

        public int Executions => executions;

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == Failing ? new FailingFactory(() => Interlocked.Increment(ref executions)) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() =>
            [new FailingFactory(() => Interlocked.Increment(ref executions))];
    }

    private sealed class FailingFactory(Action count) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Failing;
        public string DisplayName => "Fails";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new FailingBlock(count);
    }

    private sealed class FailingBlock(Action count) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
            CancellationToken cancellationToken)
        {
            count();
            throw new HttpRequestException("the broker refused the connection");
        }
    }
}
