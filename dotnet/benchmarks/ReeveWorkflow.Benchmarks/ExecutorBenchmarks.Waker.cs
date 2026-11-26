using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Benchmarks;

public partial class ExecutorBenchmarks
{
    private const int ParkedRuns = 1000;
    private const string WaitTemplateText = "${parameter.threshold} > 10 && ${parameter.mode} == \"auto\"";
    private const string ParkedParameters =
        """[{"name":"threshold","type":"Number","isRequired":true},{"name":"mode","type":"String","isRequired":true}]""";
    private static readonly DateTimeOffset ScanInstant = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private WorkflowSuspensionWaker? waker;
    private ParkedStore? parkedStore;
    private CountingLogger? wakerLog;

    private void SetupWaker()
    {
        var compiler = world!.Compiler();
        var workflowId = world.AddWorkflow(ParkedWorkflow(), inputParametersJson: ParkedParameters);
        var version = world.Store.Workflows[workflowId].CurrentVersion!;
        var program = compiler.EnsureWithProgramAsync(version, null, default).GetAwaiter().GetResult().Program;
        Require(program is not null, "the parked workflow must compile");
        var walk = CompiledWalk.Root(program!);

        parkedStore = new ParkedStore(ParkedRows(ParkedRuns, "manual", workflowId, version.Id, walk));
        wakerLog = new CountingLogger();
        waker = NewWaker(parkedStore, new ParkedResumer(), wakerLog, compiler);

        var moved = waker.WakeOnceAsync(default).GetAwaiter().GetResult();
        Require(moved == 0 && parkedStore.Taken == 0 && wakerLog.Warnings == 0,
            $"false templates: {moved} moved, {parkedStore.Taken} taken, {wakerLog.Warnings} warnings (a template that could not be read waits too)");

        var truthy = new ParkedStore(ParkedRows(ParkedRuns, "auto", workflowId, version.Id, walk));
        var resumer = new ParkedResumer();
        var checkLog = new CountingLogger();
        var resumed = NewWaker(truthy, resumer, checkLog, compiler).WakeOnceAsync(default).GetAwaiter().GetResult();
        Require(resumed == ParkedRuns && truthy.Taken == ParkedRuns && resumer.Resumed == ParkedRuns && checkLog.Warnings == 0,
            $"true templates: {resumed} moved, {truthy.Taken} taken, {resumer.Resumed} resumed, {checkLog.Warnings} warnings");
    }

    private WorkflowSuspensionWaker NewWaker(
        ParkedStore store, ParkedResumer resumer, ILogger logger, WorkflowCompilationService compiler) =>
        new(store, resumer, logger, compiler,
            new WorkflowSuspensionWakeOptions { BatchSize = ParkedRuns, MaxConcurrentResumes = ParkedRuns },
            scopes: world!.Scopes);

    [Benchmark(Description = "Scenario 4: WorkflowSuspensionWaker pass over 1000 parked WaitTemplate runs (all false)")]
    public Task<int> WakerPassOverParkedTemplates() => waker!.WakeOnceAsync(default);

    private static string ParkedWorkflow() =>
        Definition(
            new JsonArray(
                Node("start", "WorkflowInput"), Node("read", BenchmarkWorld.Reading), Node("decide", BenchmarkWorld.Decision),
                Node("wait", BenchmarkWorld.TemplateWait, configuration: new JsonObject
                {
                    [BenchmarkWorld.ConditionInput] = ExpressionSource.Prefix + WaitTemplateText
                })),
            new JsonArray(Edge("start", "read"), Edge("read", "decide"), Edge("decide", "wait")));

    private WorkflowSuspensionCandidate[] ParkedRows(int count, string mode, Guid workflowId, Guid versionId, CompiledWalk walk)
    {
        var rows = new WorkflowSuspensionCandidate[count];
        for (var i = 0; i < count; i++)
        {
            var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["start"] = new Dictionary<string, object?> { ["threshold"] = 20.0, ["mode"] = mode },
                ["read"] = new Dictionary<string, object?> { ["temperature"] = 21.5 + i % 7, ["unit"] = "C" },
                ["decide"] = new Dictionary<string, object?> { ["heat"] = i % 2 == 0, ["reason"] = "below target" },
            };
            var executionLogId = Guid.NewGuid();
            world!.Journal.Logs[executionLogId] = new WorkflowExecutionLog(
                executionLogId, null, workflowId, versionId, null, WorkflowExecutionStatus.Running, ScanInstant.AddMinutes(-6));
            rows[i] = new WorkflowSuspensionCandidate(
                SuspensionId: Guid.NewGuid(),
                ExecutionLogId: executionLogId,
                WorkflowId: workflowId,
                BlockId: "wait",
                Kind: WorkflowSuspensionKind.WaitTemplate,
                ConfigJson: JsonSerializer.Serialize(
                    new WorkflowStoredTemplateConfig(BenchmarkWorld.ConditionInput, null, WaitTemplateText), WorkflowGraphHelpers.JsonOptions),
                StatePayloadJson: WorkflowExecutionHelpers.BuildSuspensionStatePayload(outputs, walk, null, null, "start"),
                SuspendedAtUtc: ScanInstant.AddMinutes(-5),
                TimeoutAtUtc: ScanInstant.AddMinutes(5));
        }
        return rows;
    }

    private sealed class ParkedStore(WorkflowSuspensionCandidate[] rows) : IWorkflowSuspensionResumeStore
    {
        private int taken;
        public int Taken => Volatile.Read(ref taken);

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(ScanInstant, rows, 0));

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref taken);
            var row = Array.Find(rows, r => r.SuspensionId == suspensionId)!;
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = row.SuspensionId, ExecutionLogId = row.ExecutionLogId, WorkflowId = row.WorkflowId,
                BlockId = row.BlockId, Kind = row.Kind, OutcomeJson = outcomeJson,
                StatePayloadJson = row.StatePayloadJson, SuspendedAtUtc = row.SuspendedAtUtc
            });
        }

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class ParkedResumer : IWorkflowRunResumer
    {
        private int resumed;
        public int Resumed => Volatile.Read(ref resumed);

        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref resumed);
            return Task.FromResult(WorkflowResumeOutcome.Resumed);
        }
    }

    private sealed class CountingLogger : ILogger
    {
        private int warnings;
        public int Warnings => Volatile.Read(ref warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Interlocked.Increment(ref warnings);
        }
    }
}
