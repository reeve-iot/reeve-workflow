using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class WorkflowHostDefaultsRegistrationTests
{
    private sealed class HostMetrics : IWorkflowMetrics
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

    private static readonly TimeZoneInfo PlusFiveThirty =
        TimeZoneInfo.CreateCustomTimeZone("Preview+05:30", TimeSpan.FromMinutes(330), "Preview+05:30", "Preview+05:30");

    public static TheoryData<string, int> PreviewZones => new()
    {
        { "none", 22 },
        { "setting", 3 },
        { "lookup", 3 },
    };

    [Theory]
    [MemberData(nameof(PreviewZones))]
    public async Task The_library_preview_reads_local_time_in_the_zone_a_run_reads_it_in(string configured, int localHour)
    {
        using var provider = Build(configure: o =>
        {
            if (configured == "setting")
                o.Defaults.TimeZone = PlusFiveThirty;
            if (configured == "lookup")
                o.Defaults.ResolveTimeZoneAsync = (_, _) => Task.FromResult(PlusFiveThirty);
        });
        var preview = provider.GetRequiredKeyedService<ExpressionPreviewService>(ExpressionPreviewService.LibraryServiceKey);
        preview.SetContext("workflow", new ExpressionContext(now: new DateTimeOffset(2026, 3, 5, 22, 0, 0, TimeSpan.Zero)));
        using var scope = provider.CreateScope();

        var result = await preview.PreviewAsync("${now.localhour}", "workflow", scope.ServiceProvider);

        result.Error.Should().BeNull();
        result.Value.Should().Be((long)localHour);
    }

    [Fact]
    public async Task A_host_whose_zone_setting_is_null_gets_a_preview_error_and_not_a_failed_request()
    {
        using var provider = Build(configure: o => o.Defaults.TimeZone = null!);
        var preview = provider.GetRequiredKeyedService<ExpressionPreviewService>(ExpressionPreviewService.LibraryServiceKey);
        using var scope = provider.CreateScope();

        var result = await preview.PreviewAsync("${now.localhour}", "global", scope.ServiceProvider);

        result.Ok.Should().BeFalse();
        result.Error!.Message.Should().Be("The host has no time zone to read local time in.");
    }

    [Fact]
    public void The_span_source_is_the_librarys_own_name_until_a_host_says_otherwise()
    {
        using var provider = Build();

        provider.GetRequiredService<WorkflowActivitySource>().Name.Should().Be("ReeveWorkflow",
            "an operator adds one AddSource line, and it does not name whoever adopted this library first");
    }

    [Fact]
    public void A_host_that_names_the_span_source_gets_the_one_it_named()
    {
        using var provider = Build(configure: o => o.Defaults.Tracing.ActivitySourceName = "acme.dispatch");

        provider.GetRequiredService<WorkflowActivitySource>().Name.Should().Be("acme.dispatch");
    }

    [Theory]
    [InlineData(typeof(IWorkflowStore), typeof(EfWorkflowStore))]
    [InlineData(typeof(IWorkflowRunJournal), typeof(EfWorkflowRunJournal))]
    [InlineData(typeof(IWorkflowRunScopeFactory), typeof(EfWorkflowRunScopeFactory))]
    [InlineData(typeof(IWorkflowRunStateStore), typeof(EfWorkflowRunStateStore))]
    [InlineData(typeof(IWorkflowRunRecoveryStore), typeof(EfWorkflowRunRecoveryStore))]
    [InlineData(typeof(IWorkflowMetrics), typeof(MeterWorkflowMetrics))]
    [InlineData(typeof(IWorkflowNodeObserver), typeof(NullWorkflowNodeObserver))]
    [InlineData(typeof(IWorkflowExecutionGate), typeof(AlwaysRunningExecutionGate))]
    [InlineData(typeof(IWorkflowAuditSink), typeof(LoggingWorkflowAuditSink))]
    [InlineData(typeof(IWorkflowAuthorization), typeof(ConfiguredWorkflowAuthorization))]
    [InlineData(typeof(IWorkflowBlocks), typeof(RegisteredWorkflowBlocks))]
    public void Every_seam_the_library_can_answer_for_itself_resolves_after_AddWorkflows_alone(
        Type seam, Type expected)
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(seam).Should().BeOfType(expected);
    }

    [Fact]
    public void A_run_can_park_and_be_moved_again_with_nothing_written_by_the_host()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IWorkflowSuspensionStore>().Should().BeOfType<EfWorkflowSuspensionStore>();
        scope.ServiceProvider.GetService<IWorkflowSuspensionResumeStore>()
            .Should().BeOfType<EfWorkflowSuspensionResumeStore>();
    }

    [Fact]
    public void A_host_registration_written_before_AddWorkflows_is_the_one_that_answers()
    {
        using var provider = Build(before: services => services.AddSingleton<IWorkflowMetrics, HostMetrics>());

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeOfType<HostMetrics>();
    }

    [Fact]
    public void A_host_registration_written_after_AddWorkflows_is_the_one_that_answers()
    {
        using var provider = Build(after: services => services.AddSingleton<IWorkflowMetrics, HostMetrics>());

        provider.GetRequiredService<IWorkflowMetrics>().Should().BeOfType<HostMetrics>();
    }

    [Fact]
    public void The_default_authorization_grants_what_the_options_named_and_nothing_else()
    {
        using var provider = Build(
            configure: options => options.Defaults.GrantedCapabilities.Add(WorkflowCapabilities.Scripting));

        var authorization = (ConfiguredWorkflowAuthorization)provider.GetRequiredService<IWorkflowAuthorization>();

        authorization.GrantedCapabilities.Should().BeEquivalentTo([WorkflowCapabilities.Scripting]);
    }

    [Fact]
    public void The_default_authorization_grants_nothing_when_the_options_named_nothing()
    {
        using var provider = Build();

        var authorization = (ConfiguredWorkflowAuthorization)provider.GetRequiredService<IWorkflowAuthorization>();

        authorization.GrantedCapabilities.Should().BeEmpty();
    }

    [Fact]
    public void Run_state_is_tracked_because_the_persistence_package_brought_a_store_with_it()
    {
        using var provider = Build();

        new WorkflowTransitionRecorder(
                provider.GetService<IWorkflowRunStateStore>(), NullLogger.Instance)
            .TracksRunState.Should().BeTrue();
    }

    [Fact]
    public void No_block_is_sandboxed_until_a_host_says_which_are_strangers()
    {
        using var provider = Build();

        provider.GetService<IWorkflowBlockSandbox>().Should().BeNull();
    }

    [Fact]
    public void A_replica_name_one_character_past_its_column_is_refused_where_the_host_states_it()
    {
        var refusal = FluentActions.Invoking(
                () => Build(configure: options => options.Defaults.ReplicaId = new string('r', 101)))
            .Should().Throw<InvalidOperationException>();

        refusal.Which.Message.Should().Contain("101").And.Contain("100")
            .And.Contain("ReplicaId", "the line names the setting the host stated");
    }

    [Fact]
    public void A_replica_name_that_exactly_fills_its_column_is_taken()
    {
        var name = new string('r', WorkflowHostDefaultsOptions.ReplicaIdColumnWidth);

        using var provider = Build(configure: options => options.Defaults.ReplicaId = name);

        provider.GetRequiredService<WorkflowClusterIdentity>().ReplicaId.Should().Be(name,
            "the bound is the column's, so a name the column holds is not refused");
    }
}
