using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Runs the waker's pass on a timer, on every replica. Taking a parked run is a compare-and-swap,
/// so several of these over one table are safe and none of them is elected.</summary>
/// <remarks>A tick returns once its takes are done; the runs it took re-enter on the waker's workers, and the
/// next tick fires on time whatever blocks they are in. Stopping waits for those runs to finish or hand over.
///
/// It also subscribes to the host's wake source for as long as it runs, so a host that knows a
/// parked run is ready moves it then rather than at the next tick. An unfilled source is a service that polls,
/// which is the service every host had before that release.</remarks>
public sealed class WorkflowSuspensionWakeService(
    WorkflowSuspensionWaker waker,
    WorkflowSuspensionWakeOptions options,
    ILogger logger,
    WorkflowSuspensionParkSignal parkSignal,
    IWorkflowSuspensionWakeSource? wakeSource = null) : BackgroundService
{
    private readonly Lock wakesGate = new();
    private readonly HashSet<Task> wakesInFlight = [];
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private CancellationTokenSource? stopping;
    private IDisposable? parkSubscription;
    private IDisposable? wakeSubscription;
    private bool draining;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref stopping, source, null) is not null)
        {
            source.Dispose();
            throw new InvalidOperationException("This service has been started already.");
        }

        var token = source.Token;
        try
        {
            parkSubscription = parkSignal.Subscribe(SignalPass);
            wakeSubscription = wakeSource?.Subscribe((wake, publisherToken) => TrackedWakeAsync(wake, publisherToken, token));
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            Unsubscribe();
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { CancelStopping(); }
        finally
        {
            Unsubscribe();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        try { await DrainAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        var source = Interlocked.Exchange(ref stopping, null);
        Unsubscribe();
        try { source?.Cancel(); }
        finally
        {
            source?.Dispose();
            base.Dispose();
        }
    }

    private void CancelStopping()
    {
        try { stopping?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Unsubscribe()
    {
        Interlocked.Exchange(ref wakeSubscription, null)?.Dispose();
        Interlocked.Exchange(ref parkSubscription, null)?.Dispose();
    }

    private async Task DrainAsync()
    {
        await WakesInFlightAsync().ConfigureAwait(false);
        await waker.WaitForResumesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var delayBeforeNextPass = options.PollInterval;
            Task? signal = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(delayBeforeNextPass, delayCancellation.Token);
                signal ??= wakeSignal.WaitAsync(stoppingToken);
                await Task.WhenAny(delay, signal).ConfigureAwait(false);
                delayCancellation.Cancel();
                if (stoppingToken.IsCancellationRequested) return;
                if (signal.IsCompleted) signal = null;
                var passStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    await waker.TakeOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The suspension waker's pass failed.");
                }
                var remaining = waker.NextPassDelay - Stopwatch.GetElapsedTime(passStartedAt);
                delayBeforeNextPass = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Unsubscribe();

            await DrainAsync().ConfigureAwait(false);
        }
    }

    private void SignalPass()
    {
        try { wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private Task TrackedWakeAsync(
        WorkflowSuspensionWake wake, CancellationToken cancellationToken, CancellationToken stoppingToken)
    {
        var tracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (wakesGate)
        {
            if (draining) return Task.CompletedTask;
            wakesInFlight.Add(tracked.Task);
        }

        return RunAsync();

        async Task RunAsync()
        {
            try
            {
                await OnWakeAsync(wake, cancellationToken, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                tracked.TrySetResult();
                lock (wakesGate)
                    wakesInFlight.Remove(tracked.Task);
            }
        }
    }

    private async Task WakesInFlightAsync()
    {
        Task[] running;
        lock (wakesGate)
        {
            draining = true;
            running = [.. wakesInFlight];
        }

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task OnWakeAsync(
        WorkflowSuspensionWake wake, CancellationToken cancellationToken, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        try
        {
            SignalPass();
            await waker.WakeNowAsync(wake, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            logger.LogError(ex,
                "A wake for suspension {SuspensionId} / execution {ExecutionLogId} failed; the row is still "
                + "parked and a pass reaches it.", wake.SuspensionId, wake.ExecutionLogId);
        }
    }
}
