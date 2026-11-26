using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowRunStateWriteModeRegistrationTests : IDisposable
{
    private const string Peek = "test.peek";
    private const string Once = "test.once";

    private readonly SqliteConnection connection = new("Filename=:memory:");
    private readonly ConcurrentQueue<string> seen = new();

    public WorkflowRunStateWriteModeRegistrationTests() => connection.Open();

    [Fact]
    public async Task By_default_every_block_finds_its_own_name_on_the_row()
    {
        using var provider = Build();

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(Seed(provider), null, null);

        seen.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task A_heartbeat_mode_the_call_stated_leaves_the_row_at_the_first_block_for_the_run()
    {
        using var provider = Build(options => options.RunStateWriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(30)));

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(Seed(provider), null, null);

        seen.Should().Equal(["a", "a", "a"], "thirty seconds do not pass in this run, so only the first block writes");
    }

    [Fact]
    public async Task A_checkpoint_cap_the_call_stated_is_the_one_the_walk_writes_under()
    {
        using var provider = Build(options => options.MaxCheckpointBytes = 16);
        var checkpoints = new ConcurrentQueue<string>();

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(Seed(provider, checkpoints), null, null);

        WorkflowRunCheckpoint.Read(checkpoints.First())!.NotReenterableBecause.Should().Be(WorkflowRunCheckpoint.TooLarge);
    }

    [Fact]
    public async Task A_workflow_s_own_policy_is_what_its_run_writes_under()
    {
        using var provider = Build();

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(
            Seed(provider, checkpointPolicyJson: """{"Mode":"Heartbeat","HeartbeatSeconds":30}"""), null, null);

        seen.Should().Equal(["a", "a", "a"],
            "the engine writes before every block and this workflow says otherwise, which is the whole point of the column");
    }

    [Fact]
    public async Task A_workflow_s_own_policy_may_write_more_often_than_the_engine_would()
    {
        using var provider = Build(options => options.RunStateWriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(30)));

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(
            Seed(provider, checkpointPolicyJson: """{"Mode":"EveryBlock"}"""), null, null);

        seen.Should().Equal(["a", "b", "c"], "the column is read in both directions, not only as a way to write less");
    }

    [Fact]
    public async Task A_workflow_without_a_policy_writes_under_the_engine_s_mode()
    {
        using var provider = Build(options => options.RunStateWriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(30)));

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(Seed(provider), null, null);

        seen.Should().Equal(["a", "a", "a"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Mode":"Whenever"}""")]
    [InlineData("""{"Mode":"Heartbeat"}""")]
    [InlineData("""{"Mode":"EveryBlock","HeartbeatSeconds":30}""")]
    public async Task A_policy_that_will_not_read_leaves_the_run_on_the_engine_s_mode(string policyJson)
    {
        using var provider = Build(options => options.RunStateWriteMode = RunStateWriteMode.BeforeNonIdempotentOnly);
        var checkpoints = new ConcurrentQueue<string>();

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(
            Seed(provider, checkpoints, checkpointPolicyJson: policyJson), null, null);

        seen.Should().Equal(["a", "a", "a"],
            "a column nobody can read must not decide how a run is written, and every block in this chain is idempotent");
        WorkflowRunCheckpoint.Read(checkpoints.First())!.WriteMode
            .Should().Be(nameof(RunStateWriteModeKind.BeforeNonIdempotentOnly));
    }

    [Fact]
    public async Task A_policy_the_host_s_settings_no_longer_allow_leaves_the_run_on_the_engine_s_mode()
    {
        using var provider = Build(options => options.Recovery.AbandonedAfter = TimeSpan.FromSeconds(30));

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(
            Seed(provider, checkpointPolicyJson: """{"Mode":"Heartbeat","HeartbeatSeconds":90}"""), null, null);

        seen.Should().Equal(["a", "b", "c"], "which is the engine's EveryBlock, not the ninety seconds the column asks for");
    }

    [Fact]
    public async Task A_run_names_the_mode_its_own_policy_gave_it_in_its_checkpoint()
    {
        using var provider = Build();
        var checkpoints = new ConcurrentQueue<string>();

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(
            Seed(provider, checkpoints, checkpointPolicyJson: """{"Mode":"Heartbeat","HeartbeatSeconds":30}"""), null, null);

        WorkflowRunCheckpoint.Read(checkpoints.First())!.WriteMode.Should().Be(nameof(RunStateWriteModeKind.Heartbeat),
            "a recovery reads how far behind the checkpoint may be from the checkpoint, so a policy edited "
            + "mid-flight cannot change how a run already walking is re-entered");
    }

    [Fact]
    public void A_write_mode_that_skips_writes_is_refused_without_the_run_heartbeat()
    {
        var build = () => Build(options =>
        {
            options.RunStateWriteMode = RunStateWriteMode.BeforeNonIdempotentOnly;
            options.RunHeartbeatInterval = null;
        });

        build.Should().Throw<InvalidOperationException>().WithMessage("*RunStateWriteMode*RunHeartbeatInterval*",
            "between two writes the heartbeat is the only fence a replica whose row a peer took runs into");
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    public void A_heartbeat_mode_as_long_as_the_cutoff_is_refused(int seconds)
    {
        var build = () => Build(options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromMinutes(1);
            options.RunStateWriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(seconds));
        });

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*RunStateWriteMode*AbandonedAfter*");
    }

    [Fact]
    public void A_heartbeat_mode_shorter_than_the_cutoff_with_the_heartbeat_on_is_accepted()
    {
        var build = () => Build(options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromMinutes(1);
            options.RunStateWriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(59));
        });

        build.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_checkpoint_cap_that_fits_nothing_is_refused(int bytes)
    {
        var build = () => Build(options => options.MaxCheckpointBytes = bytes);

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*MaxCheckpointBytes*");
    }

    [Fact]
    public void No_write_mode_is_refused()
    {
        var build = () => Build(options => options.RunStateWriteMode = null!);

        build.Should().Throw<ArgumentNullException>().WithMessage("*RunStateWriteMode*");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, null, false)]
    public async Task A_block_that_may_not_run_twice_waits_for_its_write_exactly_where_the_registered_sweep_re_enters(
        bool recovery, bool? handsOver, bool refused)
    {
        using var provider = Build(options =>
        {
            options.Recovery.Enabled = recovery;
            options.HandOverRunsOnStop = handsOver;
        }, store => new WritesFailBefore(store, "once"));

        var run = provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(Seed(provider, onceAtTheEnd: true), null, null);

        if (refused)
        {
            await run.Awaiting(r => r).Should().ThrowAsync<WorkflowCheckpointNotWrittenException>();
            onceRan.Should().Be(0);
        }
        else
        {
            await run;
            onceRan.Should().Be(1);
        }
    }

    private ServiceProvider Build(
        Action<WorkflowEngineOptions>? engine = null, Func<IWorkflowRunStateStore, IWorkflowRunStateStore>? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        IWorkflowBlocks blocks = new Blocks(this);
        services.AddSingleton(blocks);

        var builder = services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(connection));
        });
        builder.AddWorkflowEngine(engine);
        if (store is not null)
        {
            var registered = services.Last(descriptor => descriptor.ServiceType == typeof(IWorkflowRunStateStore));
            services.Remove(registered);
            services.AddSingleton(sp => store((IWorkflowRunStateStore)registered.ImplementationFactory!(sp)));
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        this.provider = provider;
        return provider;
    }

    private ServiceProvider? provider;
    private int onceRan;
    private ConcurrentQueue<string>? variables;

    private static Guid Seed(
        IServiceProvider provider, ConcurrentQueue<string>? variables = null, bool onceAtTheEnd = false,
        string? checkpointPolicyJson = null)
    {
        const string onceJson = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"a","blockTypeId":"test.peek","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"once","blockTypeId":"test.once","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"a","mappings":{}},
                      {"id":"e2","sourceNodeId":"a","targetNodeId":"once","mappings":{}}]}
            """;
        const string chainJson = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"a","blockTypeId":"test.peek","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"b","blockTypeId":"test.peek","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"c","blockTypeId":"test.peek","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"a","mappings":{}},
                      {"id":"e2","sourceNodeId":"a","targetNodeId":"b","mappings":{}},
                      {"id":"e3","sourceNodeId":"b","targetNodeId":"c","mappings":{}}]}
            """;
        var definitionJson = onceAtTheEnd ? onceJson : chainJson;

        var tests = provider.GetRequiredService<IWorkflowBlocks>() is Blocks blocks ? blocks.Owner : null;
        tests!.variables = variables;

        var workflowId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(workflowId, "wf", null, ownerId, now);
        workflow.SetCheckpointPolicyJson(checkpointPolicyJson, now);
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

    private void Look()
    {
        using var scope = provider!.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionStates.AsNoTracking().Single();
        seen.Enqueue(row.CurrentBlockPath);
        variables?.Enqueue(row.VariablesJson);
    }

    public void Dispose() => connection.Dispose();

    private sealed class Blocks(WorkflowRunStateWriteModeRegistrationTests owner) : IWorkflowBlocks
    {
        public WorkflowRunStateWriteModeRegistrationTests Owner => owner;

        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            Peek => new PeekFactory(owner),
            Once => new OnceFactory(owner),
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new PeekFactory(owner), new OnceFactory(owner)];
    }

    private sealed class OnceFactory(WorkflowRunStateWriteModeRegistrationTests owner) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Once;
        public string DisplayName => "Once";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => false;
        public IWorkflowBlockPlugin Create() => new OnceBlock(owner);
    }

    private sealed class OnceBlock(WorkflowRunStateWriteModeRegistrationTests owner) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.onceRan);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class PeekFactory(WorkflowRunStateWriteModeRegistrationTests owner) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Peek;
        public string DisplayName => "Peek";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new PeekBlock(owner);
    }

    private sealed class PeekBlock(WorkflowRunStateWriteModeRegistrationTests owner) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            owner.Look();
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["pad"] = "0123456789" });
        }
    }
}
