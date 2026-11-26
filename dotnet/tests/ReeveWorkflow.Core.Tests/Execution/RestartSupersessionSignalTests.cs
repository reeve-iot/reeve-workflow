using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public class RestartSupersessionSignalTests
{
    private const string Channel = "dispatch:workflow-restart-cancel";

    [Fact]
    public async Task A_superseding_restart_publishes_on_the_channel_the_host_named()
    {
        var transport = new RecordingSignal();
        var workflowId = Guid.NewGuid();
        using var coordinator = Build(workflowId, transport);

        using var second = await SupersedeAsync(coordinator);

        transport.Published.Should().Contain(published => published.Channel == Channel);
        transport.Published.Should().AllSatisfy(published =>
        {
            published.Channel.Should().Be(Channel);
            published.Id.Should().Be(workflowId);
            long.TryParse(published.Payload, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _).Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_peers_cancel_arriving_on_that_channel_tears_this_replicas_run_down()
    {
        var transport = new RecordingSignal();
        var workflowId = Guid.NewGuid();
        using var coordinator = Build(workflowId, transport);
        using var lease = await AcquireAsync(coordinator);

        await transport.DeliverAsync(Channel, workflowId, null);

        lease.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancel_for_another_workflow_is_not_this_workflows()
    {
        var transport = new RecordingSignal();
        using var coordinator = Build(Guid.NewGuid(), transport);
        using var lease = await AcquireAsync(coordinator);

        await transport.DeliverAsync(Channel, Guid.NewGuid(), null);

        lease.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_transport_a_restart_is_local_only()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);

        using var second = await SupersedeAsync(coordinator);

        second.Token.IsCancellationRequested.Should().BeFalse("the newer run is the one that survives");
    }

    private static WorkflowRunCoordinator Build(Guid workflowId, IWorkflowClusterSignal transport) =>
        new(workflowId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop,
            NullWorkflowMetrics.Instance, logger: null, lockProvider: null, slotGate: null,
            isMultiReplica: false, clusterSignal: transport, restartChannel: Channel);

    private static async Task<WorkflowRunLease> AcquireAsync(WorkflowRunCoordinator coordinator)
    {
        var decision = await coordinator.AcquireAsync(CancellationToken.None);
        decision.Lease.Should().NotBeNull("a Restart always takes the permit it just cancelled");
        return decision.Lease!;
    }

    private static async Task<WorkflowRunLease> SupersedeAsync(WorkflowRunCoordinator coordinator)
    {
        var first = await AcquireAsync(coordinator);
        var second = Task.Run(() => coordinator.AcquireAsync(CancellationToken.None));

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!first.Token.IsCancellationRequested)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(5, timeout.Token);
            }
        }

        first.Dispose();
        var decision = await second;
        decision.Lease.Should().NotBeNull();
        return decision.Lease!;
    }

    private sealed class RecordingSignal : IWorkflowClusterSignal
    {
        private readonly List<Func<Guid, string?, CancellationToken, Task>> handlers = [];

        public List<(string Channel, Guid Id, string? Payload)> Published { get; } = [];

        public Task PublishAsync(string channel, Guid id, string? payload, CancellationToken cancellationToken = default)
        {
            lock (Published) Published.Add((channel, id, payload));
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
        {
            lock (handlers) handlers.Add(handler);
            return new Subscription();
        }

        public async Task DeliverAsync(string channel, Guid id, string? payload)
        {
            Func<Guid, string?, CancellationToken, Task>[] snapshot;
            lock (handlers) snapshot = [.. handlers];
            foreach (var handler in snapshot)
                await handler(id, payload, CancellationToken.None);
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
