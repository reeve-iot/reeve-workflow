using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class WorkflowHostSeamSlotTests
{
    private class HostMetrics : IWorkflowMetrics
    {
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class OtherMetrics : HostMetrics;

    private sealed class HostAuthorization : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(
            Guid principalId, string capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class HostBlocks : IWorkflowBlocks
    {
        public Abstractions.IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == "the.one" ? Only : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<Abstractions.IWorkflowBlockFactory> GetAll() => [Only];
        private static readonly Abstractions.IWorkflowBlockFactory Only = new OneBlock();
    }

    private sealed class OneBlock : Abstractions.IWorkflowBlockFactory
    {
        public string BlockTypeId => "the.one";
        public string DisplayName => "The one";
        public string Description => "The only block this host has.";
        public string Category => "test";
        public IReadOnlyCollection<Abstractions.BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<Abstractions.BlockPortDefinition> Outputs => [];
        public Abstractions.IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class HostCatalog : IWorkflowCatalog
    {
        public Task<IReadOnlyCollection<Abstractions.WorkflowSummaryResponse>> GetWorkflowsAsync(
            Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.WorkflowResponse> GetWorkflowAsync(
            Guid userId, Guid workflowId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.WorkflowImportValidationResult> ValidateWorkflowImportAsync(
            string? definitionJson, string? inputParametersJson = null, string? outputParametersJson = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Abstractions.WorkflowExecutionResponse>> GetExecutionsAsync(
            Guid userId, Guid workflowId, bool? hasErrors = null, string? status = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.PagedWorkflowExecutionsResponse> GetExecutionsPagedAsync(
            Guid userId, Guid workflowId, int skip = 0, int take = 50, bool? hasErrors = null,
            string? status = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.WorkflowExecutionDetailResponse> GetExecutionAsync(
            Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Abstractions.WorkflowExecutionStepResponse>> GetExecutionReplayAsync(
            Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Abstractions.WorkflowVersionSummaryResponse>> GetWorkflowVersionsAsync(
            Guid userId, Guid workflowId, int page = 1, int pageSize = 20,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.WorkflowVersionResponse> GetWorkflowVersionAsync(
            Guid userId, Guid workflowId, int versionNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Abstractions.WorkflowDiffResponse> DiffVersionsAsync(
            Guid userId, Guid workflowId, int versionA, int versionB, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class HostSandbox : IWorkflowBlockSandbox
    {
        public Task<WorkflowBlockSandbox?> SandboxAsync(
            Abstractions.IWorkflowBlockFactory factory, IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowBlockSandbox?>(null);
    }

    private sealed class HostSuspensions : IWorkflowSuspensionStore
    {
        public Task<Core.Model.WorkflowSuspension> SuspendAsync(
            SuspendRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void RegisterForResumption(Core.Model.WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }
    }

    [Fact]
    public void A_host_that_names_its_blocks_once_has_answered_the_walk_and_the_palette_both()
    {
        using var provider = Build(configure: options => options.Seams.Blocks.Use<HostBlocks>());
        var blocks = provider.GetRequiredService<IWorkflowBlocks>();

        blocks.Find("the.one").Should().NotBeNull();
        blocks.GetAll().Should().ContainSingle()
            .Which.BlockTypeId.Should().Be(
                "the.one", "the palette and the walk must be reading one collection, not two");
    }

    [Fact]
    public void The_execution_gate_takes_a_function_rather_than_a_class()
    {
        var suspended = true;

        using var provider = Build(configure: options => options.Seams.ExecutionGate.Use(() => suspended));
        var gate = provider.GetRequiredService<IWorkflowExecutionGate>();
        var whileSuspended = gate.IsExecutionSuspended;
        suspended = false;

        whileSuspended.Should().BeTrue();
        gate.IsExecutionSuspended.Should().BeFalse("the gate is read per run attempt, not captured once");
    }

    private static ServiceProvider Build(
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        Action<WorkflowPersistenceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        before?.Invoke(services);
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(new SqliteConnection("Filename=:memory:")));
            configure?.Invoke(options);
        });
        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void A_named_seam_beats_a_separate_registration_written_before_AddWorkflows()
    {
        using var provider = Build(
            before: services => services.AddSingleton<IWorkflowMetrics, OtherMetrics>(),
            configure: options => options.Seams.Metrics.Use<HostMetrics>());

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeOfType<HostMetrics>();
    }

    [Fact]
    public void A_named_seam_beats_the_library_default_without_the_host_knowing_call_order()
    {
        using var provider = Build(configure: options => options.Seams.Metrics.Use<HostMetrics>());

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeOfType<HostMetrics>();
    }

    [Fact]
    public void A_slot_left_alone_still_yields_to_a_separate_registration()
    {
        using var provider = Build(before: services => services.AddSingleton<IWorkflowMetrics, OtherMetrics>());

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeOfType<OtherMetrics>();
    }

    [Fact]
    public void A_slot_filled_without_a_lifetime_is_a_singleton_except_the_store_and_the_journal_which_are_scoped()
    {
        var seams = new WorkflowPersistenceOptions().Seams;

        seams.Metrics.Use<HostMetrics>().Lifetime.Should().Be(ServiceLifetime.Singleton);
        seams.Catalog.Use(_ => new HostCatalog()).Lifetime.Should().Be(ServiceLifetime.Singleton);
        seams.Store.Use<EfWorkflowStore>().Lifetime.Should().Be(ServiceLifetime.Scoped);
        seams.RunJournal.Use(sp => new EfWorkflowRunJournal(sp.GetRequiredService<WorkflowDbContext>())).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        seams.Store.Use<EfWorkflowStore>(ServiceLifetime.Singleton).Lifetime.Should().Be(ServiceLifetime.Singleton,
            "a lifetime the host names is the one registered");
    }

    [Fact]
    public void A_factory_given_a_lifetime_other_than_its_slots_default_is_registered_with_that_lifetime()
    {
        var seams = new WorkflowPersistenceOptions().Seams;

        seams.Store.Use(sp => new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>()), ServiceLifetime.Singleton)
            .Lifetime.Should().Be(ServiceLifetime.Singleton, "the store's default is scoped, and the host named another");
        seams.Catalog.Use(_ => new HostCatalog(), ServiceLifetime.Scoped).Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void A_scoped_seam_is_registered_scoped()
    {
        using var provider = Build(
            configure: options => options.Seams.Authorization.Use<HostAuthorization>(ServiceLifetime.Scoped));

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<IWorkflowAuthorization>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<IWorkflowAuthorization>());
    }

    [Fact]
    public void An_instance_the_host_already_holds_can_fill_a_slot()
    {
        var mine = new HostMetrics();
        using var provider = Build(configure: options => options.Seams.Metrics.UseInstance(mine));

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeSameAs(mine);
    }

    [Fact]
    public void An_optional_seam_with_no_default_is_absent_until_a_host_names_one()
    {
        using var without = Build();
        without.GetService<IWorkflowBlockSandbox>().Should().BeNull();

        using var with = Build(configure: options => options.Seams.BlockSandbox.Use<HostSandbox>());
        with.GetRequiredService<IWorkflowBlockSandbox>().Should().BeOfType<HostSandbox>();
    }

    [Fact]
    public void A_host_with_its_own_place_to_park_a_run_replaces_the_one_this_package_fills()
    {
        using var with = Build(configure: options => options.Seams.SuspensionStore.Use<HostSuspensions>());

        with.GetRequiredService<IWorkflowSuspensionStore>().Should().BeOfType<HostSuspensions>();
    }

    [Fact]
    public void An_unfilled_slot_reads_as_the_library_answer_and_a_filled_one_reads_as_the_host()
    {
        var options = new WorkflowPersistenceOptions();
        options.Seams.Metrics.Use<HostMetrics>();

        options.Seams.Metrics.IsHostSupplied.Should().BeTrue();
        options.Seams.NodeObserver.IsHostSupplied.Should().BeFalse();
    }

    [Fact]
    public void The_defaults_still_answer_every_slot_a_host_leaves_alone()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkflowStore>().Should().BeOfType<EfWorkflowStore>();
        scope.ServiceProvider.GetRequiredService<IWorkflowAuditSink>().Should().BeOfType<LoggingWorkflowAuditSink>();
        scope.ServiceProvider.GetRequiredService<IWorkflowCatalog>().Should().BeOfType<WorkflowCatalog>();
    }

    [Fact]
    public void The_read_side_the_HTTP_endpoints_resolve_is_registered_by_AddWorkflows()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IWorkflowCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void A_host_can_answer_the_read_side_itself()
    {
        using var provider = Build(configure: options => options.Seams.Catalog.Use<HostCatalog>(ServiceLifetime.Scoped));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkflowCatalog>().Should().BeOfType<HostCatalog>();
    }
}
