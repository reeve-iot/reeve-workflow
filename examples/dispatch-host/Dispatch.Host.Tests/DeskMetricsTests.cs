using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace Dispatch.Host.Tests;

public sealed class DeskMetricsTests
{
    [Fact]
    public void The_desk_writes_no_metrics_code_and_gets_a_meter_an_exporter_can_read()
    {
        var points = new ConcurrentQueue<(Meter Meter, string Name, string? TriggerType)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkflowTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => points.Enqueue((instrument.Meter, instrument.Name, TriggerTypeOf(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => points.Enqueue((instrument.Meter, instrument.Name, TriggerTypeOf(tags))));
        listener.Start();

        using var app = DispatchHost.Build(new DispatchHostOptions
        {
            PostgresConnectionString = "Host=127.0.0.1;Port=1;Database=desk;Username=u;Password=p",
            MigrateOnStartup = false,
        });
        var metrics = app.Services.GetRequiredService<IWorkflowMetrics>();
        var triggerType = $"depot-{Guid.NewGuid():N}";
        metrics.RecordExecutionStarted();
        metrics.RecordExecutionCompleted(WorkflowRunStatusLabels.Succeeded, 0.5, triggerType);

        var ours = points.Where(p => p.TriggerType == triggerType).Select(p => p.Meter).Distinct().Should().ContainSingle().Subject;
        points.Where(p => ReferenceEquals(p.Meter, ours)).Select(p => p.Name).Should().BeEquivalentTo(
            ["workflow.runs.started", "workflow.runs.completed", "workflow.run.duration"],
            "a host that fills no metrics seam should still have something to point AddMeter at");
    }

    private static string? TriggerTypeOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "trigger_type")
                return tag.Value as string;
        }

        return null;
    }
}
