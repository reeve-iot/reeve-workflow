using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.Core.Tests;

public class MeterWorkflowMetricsTests
{
    [Fact]
    public void A_run_that_started_and_finished_is_two_instruments_and_a_duration()
    {
        using var recorded = Listen(out var options);
        using var metrics = new MeterWorkflowMetrics(options);

        metrics.RecordExecutionStarted();
        metrics.RecordExecutionCompleted(WorkflowRunStatusLabels.Succeeded, 1.5, "schedule");

        recorded.Names.Should().BeEquivalentTo([options.RunsStarted, options.RunsCompleted, options.RunDuration]);
        recorded.ValueOf(options.RunDuration).Should().Be(1.5);
    }

    [Fact]
    public void Nothing_this_records_carries_an_id_as_a_tag()
    {
        using var recorded = Listen(out var options);
        using var metrics = new MeterWorkflowMetrics(options);

        metrics.RecordExecutionStarted();
        metrics.RecordExecutionCompleted(WorkflowRunStatusLabels.Failed, 0.25, "device");
        metrics.RecordNodeError("http.request", "retry", caught: true, "TimeoutException");
        metrics.RecordSupersession(Guid.NewGuid(), "Restart", WorkflowAdmissionOutcomes.Restarted);
        metrics.RecordDebugSession(WorkflowDebugSessionOutcomes.Killed);
        metrics.RecordDebugPauseDuration(4);
        metrics.RecordSuspensionStarted("wait-for-trigger");
        metrics.RecordSuspensionResolved("wait-for-trigger", "resumed", 30);

        recorded.TagValues.Should().NotContain(v => IsGuid(v));
        recorded.TagKeys.Should().NotContain(k => k.Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_run_with_no_trigger_type_still_lands_on_the_same_series_as_one_with_it()
    {
        using var recorded = Listen(out var options);
        using var metrics = new MeterWorkflowMetrics(options);

        metrics.RecordExecutionCompleted(WorkflowRunStatusLabels.Succeeded, 1, triggerType: null);

        recorded.TagKeys.Should().Contain("trigger_type");
    }

    [Fact]
    public void The_host_names_the_meter_and_every_instrument_on_it()
    {
        var options = new WorkflowMetricsOptions
        {
            MeterName = $"desk-{Guid.NewGuid():N}",
            RunsStarted = "desk.dispatches.opened",
        };
        using var recorded = Listen(options);
        using var metrics = new MeterWorkflowMetrics(options);

        metrics.RecordExecutionStarted();

        recorded.Names.Should().ContainSingle().Which.Should().Be("desk.dispatches.opened");
    }

    [Fact]
    public void The_default_meter_is_the_one_the_published_constant_names()
    {
        using var recorded = Listen(new WorkflowMetricsOptions());
        using var metrics = new MeterWorkflowMetrics();

        metrics.RecordExecutionStarted();

        recorded.Names.Should().Contain("workflow.runs.started");
    }

    [Fact]
    public void Every_instrument_this_publishes_is_one_some_method_records_to()
    {
        using var recorded = Listen(out var options);
        using var metrics = new MeterWorkflowMetrics(options);

        RecordEverything(metrics);

        recorded.Names.Should().BeEquivalentTo([
            options.RunsStarted, options.RunsCompleted, options.RunDuration, options.NodeErrors,
            options.RunAdmissions, options.DebugSessions, options.DebugPauseDuration,
            options.SuspensionsStarted, options.SuspensionsResolved, options.SuspensionDuration,
        ]);
    }

    [Fact]
    public void One_casing_for_label_values_however_the_caller_spelled_them()
    {
        using var recorded = Listen(out var options);
        using var metrics = new MeterWorkflowMetrics(options);

        metrics.RecordSupersession(Guid.NewGuid(), "Parallel", WorkflowAdmissionOutcomes.Started);
        metrics.RecordNodeError("http.request", "FailWorkflow", caught: false, "HttpRequestException");
        metrics.RecordSuspensionStarted("WaitForTrigger");

        recorded.TagValuesOf("mode").Should().Equal("parallel");
        recorded.TagValuesOf("policy").Should().Equal("failworkflow");
        recorded.TagValuesOf("kind").Should().Equal("waitfortrigger");
        recorded.TagValuesOf("exception_type").Should().Equal("HttpRequestException");
    }

    private static void RecordEverything(IWorkflowMetrics metrics)
    {
        metrics.RecordExecutionStarted();
        metrics.RecordExecutionCompleted(WorkflowRunStatusLabels.Succeeded, 1);
        metrics.RecordNodeError("http.request", "Retry", caught: true, "TimeoutException");
        metrics.RecordSupersession(Guid.NewGuid(), "Single", WorkflowAdmissionOutcomes.Skipped);
        metrics.RecordDebugSession(WorkflowDebugSessionOutcomes.Completed);
        metrics.RecordDebugPauseDuration(4);
        metrics.RecordSuspensionStarted("WaitForTrigger");
        metrics.RecordSuspensionResolved("WaitForTrigger", WorkflowRunStatusLabels.Succeeded, 30);
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);

    private static Recorded Listen(out WorkflowMetricsOptions options)
    {
        options = new WorkflowMetricsOptions { MeterName = $"test-{Guid.NewGuid():N}" };
        return Listen(options);
    }

    private static Recorded Listen(WorkflowMetricsOptions options)
    {
        var recorded = new Recorded(options.MeterName);
        recorded.Start();
        return recorded;
    }

    private sealed class Recorded(string meterName) : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> points = new();

        public IReadOnlyCollection<string> Names => points.Select(p => p.Name).Distinct().ToArray();

        public IReadOnlyCollection<string> TagKeys => points.SelectMany(p => p.Tags).Select(t => t.Key).Distinct().ToArray();

        public IReadOnlyCollection<string> TagValues =>
            points.SelectMany(p => p.Tags).Select(t => t.Value?.ToString() ?? string.Empty).Distinct().ToArray();

        public double ValueOf(string name) => points.Single(p => p.Name == name).Value;

        public IReadOnlyCollection<string> TagValuesOf(string key) => points
            .SelectMany(p => p.Tags).Where(t => t.Key == key)
            .Select(t => t.Value?.ToString() ?? string.Empty).Distinct().ToArray();

        public void Start()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName) l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((i, m, t, _) => points.Enqueue((i.Name, m, t.ToArray())));
            listener.SetMeasurementEventCallback<double>((i, m, t, _) => points.Enqueue((i.Name, m, t.ToArray())));
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }
}
