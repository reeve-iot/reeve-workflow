using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Runs the abandoned-run sweep on a timer, on every replica.</summary>
public sealed class WorkflowRunRecoveryService(
    WorkflowRunRecoverySweep sweep,
    WorkflowRunRecoveryOptions options,
    ILogger logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var ticks = new PeriodicTimer(options.SweepInterval);
            while (await ticks.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await sweep.ClaimOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The abandoned-run sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await sweep.WaitForResumesAsync().ConfigureAwait(false);
        }
    }
}
