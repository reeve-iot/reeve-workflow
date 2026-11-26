using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class EfWorkflowRunJournalTests
{
    private sealed class BufferingContributor : IWorkflowRunLogContributor
    {
        private readonly List<(Guid ExecutionId, string Message)> buffered = [];

        public int DrainCount { get; private set; }

        public void Add(Guid executionId, string message) => buffered.Add((executionId, message));

        public IReadOnlyList<WorkflowExecutionLogEntry> DrainEntries(Guid executionId)
        {
            DrainCount++;
            var mine = buffered.Where(b => b.ExecutionId == executionId).ToArray();
            buffered.RemoveAll(b => b.ExecutionId == executionId);
            return mine
                .Select(b => new WorkflowExecutionLogEntry(
                    Guid.NewGuid(), executionId, "node-1", "Http", LogEntryLevel.Info, b.Message,
                    dataJson: null, iterationIndex: null, callStackJson: null, DateTimeOffset.UtcNow))
                .ToArray();
        }
    }

    private static WorkflowExecutionLogEntry Entry(Guid executionId, string message) => new(
        Guid.NewGuid(), executionId, "node-1", "Log", LogEntryLevel.Info, message,
        dataJson: null, iterationIndex: null, callStackJson: null, DateTimeOffset.UtcNow);

    private static async Task<Guid> OpenRunAsync(SqliteWorkflowDatabase database, IServiceProvider scoped)
    {
        var (workflowId, versionId) = database.SeedPublishedWorkflow();
        var executionId = Guid.NewGuid();

        await scoped.GetRequiredService<IWorkflowRunJournal>().OpenRunAsync(
            new WorkflowExecutionLog(
                executionId, automationId: null, workflowId, versionId, triggerId: null,
                WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow),
            CancellationToken.None);

        return executionId;
    }

    [Fact]
    public async Task An_opened_run_is_durable_before_the_call_returns()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();

        var executionId = await OpenRunAsync(database, scope.ServiceProvider);

        using var reader = database.Services.CreateScope();
        (await reader.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowExecutionLogs.AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId))
            .Should().NotBeNull();
    }

    [Fact]
    public void Parent_result_uses_an_optional_jsonb_column_on_the_run_log()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();
        var property = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Model
            .FindEntityType(typeof(WorkflowExecutionLog))!.FindProperty("ResultOutputsJson")!;

        property.IsNullable.Should().BeTrue();
        property.GetColumnName().Should().Be("result_outputs_json");
        property.GetColumnType().Should().Be("jsonb");
    }

    [Fact]
    public async Task Entries_are_written_only_when_the_caller_asked_to_record_them()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();
        var executionId = await OpenRunAsync(database, scope.ServiceProvider);
        var journal = scope.ServiceProvider.GetRequiredService<IWorkflowRunJournal>();

        await journal.SaveRunDetailsAsync(
            executionId, recordLogEntries: false, [Entry(executionId, "engine says")], [], CancellationToken.None);

        (await Entries(database, executionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_contributor_is_drained_even_when_engine_entries_are_not_being_recorded()
    {
        var contributor = new BufferingContributor();
        using var database = new SqliteWorkflowDatabase(
            services => services.AddSingleton<IWorkflowRunLogContributor>(contributor));
        using var scope = database.Services.CreateScope();
        var executionId = await OpenRunAsync(database, scope.ServiceProvider);
        contributor.Add(executionId, "GET https://example.invalid");

        await scope.ServiceProvider.GetRequiredService<IWorkflowRunJournal>().SaveRunDetailsAsync(
            executionId, recordLogEntries: false, [Entry(executionId, "engine says")], [], CancellationToken.None);

        var written = await Entries(database, executionId);
        written.Select(e => e.Message).Should().BeEquivalentTo(["GET https://example.invalid"]);
    }

    [Fact]
    public async Task Nothing_is_drained_for_a_save_that_names_no_run()
    {
        var contributor = new BufferingContributor();
        using var database = new SqliteWorkflowDatabase(
            services => services.AddSingleton<IWorkflowRunLogContributor>(contributor));
        using var scope = database.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IWorkflowRunJournal>().SaveRunDetailsAsync(
            executionId: null, recordLogEntries: true, [], [], CancellationToken.None);

        contributor.DrainCount.Should().Be(0);
    }

    [Fact]
    public async Task Steps_are_written_whatever_the_entry_flag_says()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();
        var executionId = await OpenRunAsync(database, scope.ServiceProvider);

        await scope.ServiceProvider.GetRequiredService<IWorkflowRunJournal>().SaveRunDetailsAsync(
            executionId,
            recordLogEntries: false,
            [],
            [new WorkflowExecutionStep(
                Guid.NewGuid(), executionId, "node-1", "Log", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                durationMs: 3, WorkflowExecutionStepStatus.Succeeded, sortOrder: 0,
                inputsJson: null, outputsJson: null, errorMessage: null, errorStackTrace: null)],
            CancellationToken.None);

        using var reader = database.Services.CreateScope();
        (await reader.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowExecutionSteps.AsNoTracking().Where(s => s.ExecutionLogId == executionId).ToListAsync())
            .Should().ContainSingle();
    }

    private static async Task<List<WorkflowExecutionLogEntry>> Entries(
        SqliteWorkflowDatabase database, Guid executionId)
    {
        using var reader = database.Services.CreateScope();
        return await reader.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowExecutionLogEntries.AsNoTracking()
            .Where(e => e.ExecutionLogId == executionId)
            .ToListAsync();
    }
}
