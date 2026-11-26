using System.Text.RegularExpressions;
using Dispatch.Host.Freight;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.TestHooks;

public static class FaultHooks
{
    public const string StallAfterTakingAWait = "DISPATCH_TEST_STALL_AFTER_TAKING_A_WAIT";

    public const string RestartWordUnheard = "DISPATCH_TEST_RESTART_WORD_UNHEARD";

    public const string EventOnlyFireKeys = "DISPATCH_TEST_EVENT_ONLY_FIRE_KEYS";

    public static void Apply(IServiceCollection services)
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable(RestartWordUnheard), out var unheard) && unheard)
        {
            services.AddSingleton(new WorkflowClusterSignalChannels
            {
                RestartCancel = $"dispatch:test-unheard-restart-cancel:{Guid.NewGuid():N}"
            });
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable(EventOnlyFireKeys), out var eventOnly) && eventOnly)
        {
            var registered = services.Last(d => d.ServiceType == typeof(IWorkflowFireDedup));
            services.AddSingleton<IWorkflowFireDedup>(sp => new ForgettingTheWorkflow((IWorkflowFireDedup)(
                registered.ImplementationInstance
                ?? registered.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!))));
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable(StallAfterTakingAWait), out var stall) && stall)
        {
            var registeredStore = services.Last(d => d.ServiceType == typeof(IWorkflowSuspensionResumeStore));
            services.AddSingleton<IWorkflowSuspensionResumeStore>(sp => new StallingAfterTheTake((IWorkflowSuspensionResumeStore)(
                registeredStore.ImplementationInstance
                ?? registeredStore.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, registeredStore.ImplementationType!))));
        }
    }

    private sealed class ForgettingTheWorkflow(IWorkflowFireDedup inner) : IWorkflowFireDedup
    {
        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            inner.TryClaimFireAsync(WorkflowInKey.Replace(fireKey, DepotFeed.FireKeyPrefix), window, cancellationToken);

        public string Fingerprint(string fireKey) => inner.Fingerprint(fireKey);

        private static readonly Regex WorkflowInKey = new("depot-event:[0-9a-f]{32}:", RegexOptions.CultureInvariant);
    }

    private sealed class StallingAfterTheTake(IWorkflowSuspensionResumeStore inner) : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            inner.ScanAsync(afterPosition, limit, cancellationToken);

        public Task<WorkflowSuspensionScan> ScanAsync(
            WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken) =>
            inner.ScanAsync(after, limit, cancellationToken);

        public async Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            var taken = await inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);
            if (taken is not null)
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return taken;
        }

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);

        public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
            WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken) =>
            inner.ScanAbandonedTakesAsync(after, abandonedAfter, limit, cancellationToken);

        public Task<ResumeContext?> TryRetakeAsync(WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken) =>
            inner.TryRetakeAsync(abandoned, cancellationToken);
    }
}
