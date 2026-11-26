using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Runs <see cref="WorkflowCompilationSweepRunner"/> on a timer, when
/// <see cref="WorkflowCompilationSweepOptions.Enabled"/> is set: first once the host has started, so a host that
/// migrates before it runs is migrated, then <see cref="WorkflowCompilationSweepOptions.Interval"/> after each run, or
/// <see cref="WorkflowCompilationSweepOptions.ReadinessRetry"/> after one that found the database not ready.</summary>
/// <remarks>No exception leaves the service, so nothing it does stops the host. A run that faults has been logged by
/// the runner, and the timer waits <see cref="WorkflowCompilationSweepOptions.Interval"/> and runs again. A wait that
/// faults is logged at <see cref="LogLevel.Error"/> and ends the timer. A stop, during a run or a wait, ends the
/// service and logs nothing. The waits are on the registered <see cref="TimeProvider"/>.</remarks>
public sealed class WorkflowCompilationSweepService(
    WorkflowCompilationSweepRunner runner,
    WorkflowCompilationSweepOptions options,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<WorkflowCompilationSweepService>? logger = null) : BackgroundService
{
    private readonly ILogger log = logger ?? NullLogger<WorkflowCompilationSweepService>.Instance;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartedAsync(stoppingToken).ConfigureAwait(false);
            while (true)
            {
                var wait = await RunAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(wait, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The compilation sweep timer stopped: its wait failed. No further timed run will start.");
        }
    }

    private async Task<TimeSpan> RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            var (outcome, _) = await runner.RunAsync(stoppingToken).ConfigureAwait(false);
            return outcome == WorkflowCompilationSweepOutcome.NotReady ? options.ReadinessRetry : options.Interval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            return options.Interval;
        }
    }

    private async Task StartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        await started.Task.ConfigureAwait(false);
    }
}
