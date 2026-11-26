namespace ReeveWorkflow.Core.Hosting;

/// <summary>The order of a Restart workflow's triggers, kept where every replica reads the same answer: which
/// generation was issued last, which is the newest that started, and which are still waiting to.</summary>
/// <remarks>Restart runs the newest trigger. Until this seam the replicas ordered their triggers by
/// their own clocks and by what reached them over the cluster signal, which can be lost, and a trigger that heard of a
/// newer one gave up after a window: when the word came one window late, no trigger ran.
///
/// Here a trigger stands aside for good only for a generation that started — a fact — and waits, without a deadline
/// of its own, for a newer generation that is still waiting and live. A generation stops being live when the trigger
/// that holds it stops touching it, which is what a replica that died leaves behind, and every older trigger then goes
/// ahead. The cluster signal still carries the generation to the replica running an older run, but only to make the
/// handover quick: a replica that misses it finds the newer generation on the run's next heartbeat.</remarks>
public interface IWorkflowRestartGenerations
{
    /// <summary>Issues the workflow's next generation, larger than every one issued before, and registers it as
    /// waiting and live for <paramref name="liveFor"/>.</summary>
    Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken);

    /// <summary>Keeps a waiting generation live for another <paramref name="liveFor"/>. A generation already started
    /// or retired stays so.</summary>
    Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken);

    /// <summary>Stops a generation waiting: its trigger stood aside, or its caller gave up.</summary>
    Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken);

    /// <summary>Records that a generation's run started: the started mark rises to it when it is larger, and the
    /// generation stops waiting.</summary>
    Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken);

    /// <summary>The workflow's generations as they stand.</summary>
    Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken);
}

/// <summary>A Restart workflow's generations at one moment.</summary>
/// <param name="Issued">The last generation issued, or zero when none was.</param>
/// <param name="Started">The newest generation whose run started, or zero.</param>
/// <param name="NewestWaiting">The newest generation still waiting and live, or null when none is.</param>
public sealed record WorkflowRestartGenerationState(long Issued, long Started, long? NewestWaiting);

/// <summary>The generations of one process, which are every generation there is when there is one process.</summary>
/// <remarks>The default an engine with no peers gets. A host that has declared peers and left this seam here does
/// not start: each replica would order only its own triggers, and a Restart would run once per replica.</remarks>
public sealed class InProcessRestartGenerations(TimeProvider? clock = null) : IWorkflowRestartGenerations
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Record> records = [];

    /// <inheritdoc />
    public Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var record = RecordOf(workflowId);
            var generation = ++record.Issued;
            record.Waiting[generation] = clock.GetUtcNow() + liveFor;
            return Task.FromResult(generation);
        }
    }

    /// <inheritdoc />
    public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var record = RecordOf(workflowId);
            if (record.Waiting.ContainsKey(generation))
                record.Waiting[generation] = clock.GetUtcNow() + liveFor;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
    {
        lock (gate)
            RecordOf(workflowId).Waiting.Remove(generation);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var record = RecordOf(workflowId);
            record.Waiting.Remove(generation);
            if (generation > record.Started)
                record.Started = generation;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var record = RecordOf(workflowId);
            var now = clock.GetUtcNow();
            long? newest = null;
            foreach (var (generation, liveUntil) in record.Waiting)
            {
                if (liveUntil > now && (newest is null || generation > newest))
                    newest = generation;
            }
            return Task.FromResult(new WorkflowRestartGenerationState(record.Issued, record.Started, newest));
        }
    }

    private Record RecordOf(Guid workflowId)
    {
        if (!records.TryGetValue(workflowId, out var record))
            records[workflowId] = record = new Record();
        return record;
    }

    private sealed class Record
    {
        public long Issued;
        public long Started;
        public readonly Dictionary<long, DateTimeOffset> Waiting = [];
    }
}
