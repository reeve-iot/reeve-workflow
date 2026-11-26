using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

internal sealed class WorkflowUnwokenSleepCheck(
    IWorkflowSuspensionResumeStore resumeStore,
    IWorkflowSuspensionStore? suspensions,
    ILogger logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (suspensions is not IWorkflowParkedSleepStore)
            return;

        try
        {
            var scan = await resumeStore.ScanAsync(WorkflowSuspensionScanCursor.Start, 1, [WorkflowSuspensionKind.Sleep], cancellationToken).ConfigureAwait(false);
            if (scan.Candidates.Any(candidate => candidate.Kind == WorkflowSuspensionKind.Sleep))
            {
                logger.LogWarning(
                    "A parked Sleep is overdue, and Waker.Enabled is false, so nothing on this host wakes it. Unless the "
                    + "host wakes Sleep rows itself, enable the waker with Sleep among its Kinds until no Sleep rows remain; see 'Turning parking on and off' in docs/operations/parking.md.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not look for overdue parked sleeps at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
