using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The few things the library's own host seams cannot guess. Everything else about them is decided by
/// the seam's default implementation, so a host with nothing to say here says nothing.</summary>
/// <remarks>These configure the defaults, they do not switch them on: <c>AddWorkflows</c> registers each default
/// with <c>TryAdd</c>, and a host that registers its own implementation of a seam replaces the default whether it
/// does so before or after that call. Leaving this untouched is the supported configuration.</remarks>
public sealed class WorkflowHostDefaultsOptions
{
    /// <summary>What <c>workflow_executions_state.owned_by_replica_id</c> holds, which is what
    /// <see cref="ReplicaId"/> has to fit: the library's own table, written by every run-state write this
    /// process makes. <c>AddWorkflows</c> refuses a longer name where the host states it, because the other
    /// reading of an over-long one is a run whose park fails part-way through.</summary>
    public const int ReplicaIdColumnWidth = 100;

    /// <summary>The zone a workflow's local wall-clock expression fields are read in. UTC unless the host has a
    /// notion of place — a home, a tenant.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>The same zone, for a host where it is a lookup and not a setting. Set, it is what
    /// <see cref="TimeZone"/> would have answered, and it is asked in three places rather than one.</summary>
    /// <remarks>Once per graph walk, inside the run's own scope, so it may resolve a scoped context out of
    /// it — a walk that gets no zone has no reading for <c>${now.local}</c> and fails. And once per parked
    /// template that reads local time, from a scope of its own, because a waker's pass belongs to no run:
    /// there a failure is not fatal, and the row waits for its timeout rather than being read in the wrong
    /// zone. Fill <c>Waker.ResolveTimeZone</c> to answer that one from the row instead. And once per expression
    /// preview that reads the clock, from the request's scope, where a failure is a preview error.</remarks>
    public Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? ResolveTimeZoneAsync { get; set; }

    /// <summary>Host namespaces shared by compilation, workflow runs, parked waits and preview.</summary>
    public IList<WorkflowExpressionNamespace> ExpressionNamespaces { get; } = new List<WorkflowExpressionNamespace>();

    /// <summary>Whether entering a node is an audit event as well as a recovery row. Off by default: it is one
    /// audit write per node per run, which a host wants only where the trail is required.</summary>
    public bool AuditTransitions { get; set; }

    /// <summary>The block types whose delay is parked in the run's row rather than awaited in this process, so
    /// a replica restart mid-wait resumes instead of losing the run.</summary>
    /// <remarks>Named here because the library ships no blocks. A named block is handed no chance to run its
    /// own <c>ExecuteAsync</c>: its inputs must carry the duration, and its outputs are the runner's.</remarks>
    public IList<string> DurableSleepBlockTypeIds { get; } = [];

    /// <summary>Which process owns the run-state rows it writes, so a peer can tell a live run from an abandoned
    /// one. Null on a single-replica host, where nothing else could reclaim a row.</summary>
    /// <remarks>It is also this process's identity on the wire, which makes it a value that must
    /// differ in every replica of one deployment: two that share it read each other's signals as echoes of
    /// their own and drop every one. A machine name is not enough where two replicas share a machine.
    ///
    /// Left null — or set to whitespace, which is not an id anybody means — the transport invents an id for
    /// this process rather than publishing an empty one. A run-state row still records what is written here,
    /// so the two halves say different things until a host states a real id.</remarks>
    public string? ReplicaId { get; set; }

    /// <summary>Whether this deployment runs as more than one process. The one fact about itself a host
    /// cannot be guessed into: every cluster seam degrades to in-process behaviour when it is left alone, and
    /// in-process behaviour on several replicas is wrong without being loud. Saying true here makes the
    /// library refuse to start rather than under-enforce — see <c>Seams.RunSlotGate</c>.</summary>
    public bool IsMultiReplica { get; set; }

    /// <summary>What the library's three cross-replica channels are called on the wire. Defaults that a host
    /// already speaking them keeps; anything else is a contract between running replicas and changing one is a
    /// rollout, not a setting — see <see cref="WorkflowClusterSignalChannels"/>.</summary>
    public WorkflowClusterSignalChannels SignalChannels { get; } = new();

    /// <summary>The meter's name and its instrument names. Left alone, the metrics seam records on a meter
    /// called <c>ReeveWorkflow</c> and a host needs one <c>AddMeter</c> line and no adapter.</summary>
    public WorkflowMetricsOptions Metrics { get; } = new();

    /// <summary>The name this library's spans are published under. Left alone, an operator adds one
    /// <c>AddSource("ReeveWorkflow")</c> line; a host whose collector already names the source something else
    /// says so here rather than losing the traces.</summary>
    public WorkflowTracingOptions Tracing { get; } = new();

    /// <summary>The capability names this deployment grants to everyone — see <c>WorkflowCapabilities</c>. Empty
    /// denies every one of them, which is the reading of a host that has not been asked the question; a host with
    /// real per-principal answers registers its own <c>IWorkflowAuthorization</c> instead of filling this.</summary>
    public IList<string> GrantedCapabilities { get; } = [];
}

internal sealed record WorkflowExpressionNamespaceSnapshot(IReadOnlyList<WorkflowExpressionNamespace> Registrations);
