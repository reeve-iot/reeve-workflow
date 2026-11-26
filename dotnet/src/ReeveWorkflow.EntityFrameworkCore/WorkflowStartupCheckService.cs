using Microsoft.Extensions.Hosting;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

internal sealed class WorkflowStartupCheckService(WorkflowStartupChecks checks, IServiceProvider services)
    : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        checks.Run(services);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
