namespace ReeveWorkflow.Core.Hosting;

/// <summary>One transport for every cross-replica nudge the library sends: publish an id on a named channel,
/// hear the ids other replicas published on it, never your own.</summary>
/// <remarks>Restart supersession and the debugger's commands share this one transport.
///
/// Every signal on it is a hint, never a fact: the durable state is in the database and a lost message costs a
/// takeover or a step, not correctness. That is why an implementation must swallow transport faults rather than
/// throw, and why the library never waits for a subscriber to answer — with the one exception below.
///
/// The exception is a debugger command, whose sender waits a bounded time for the holder's confirmation on a
/// channel of its own. The wait is never what makes the command correct: the durable state is still the session's
/// status, and a lost confirmation is reported as unconfirmed rather than failed.</remarks>
public interface IWorkflowClusterSignal
{
    /// <summary>Tells the other replicas that something happened to <paramref name="id"/>. Never throws on a
    /// transport fault — a lost signal degrades the channel to this process, which is the single-replica
    /// behaviour and is safe.</summary>
    /// <param name="channel">One of <see cref="WorkflowClusterSignalChannels"/>. A wire name: every replica of
    /// one deployment must use the same string or they stop hearing each other.</param>
    /// <param name="id">What the channel is keyed by — a workflow, a debug session, an execution log.</param>
    /// <param name="payload">The channel's own encoding, or null for a channel that carries only the id.</param>
    /// <param name="cancellationToken">Cancels the send, not what it announces: the local half already happened.</param>
    Task PublishAsync(string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default);

    /// <summary>Registers a handler for what peers publish on <paramref name="channel"/>. Disposing
    /// unsubscribes.</summary>
    /// <remarks>An implementation MUST drop this replica's own broadcasts. The publisher has already acted
    /// locally, so hearing itself makes a restart cancel the run that just won the takeover.</remarks>
    IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler);
}

/// <summary>The wire names of the channels the library publishes on, as options because they are contracts
/// between running replicas rather than settings.</summary>
/// <remarks>Two replicas that disagree on one of these names do not fail: they simply stop hearing each other on
/// that channel, and everything degrades to per-process behaviour silently. Change one only with a rollout that
/// has no mixed versions, or accept that window deliberately.
///
/// The defaults are the names the library's first adopter already has on the wire, so that adopting this seam is
/// not itself a rename. A host with its own names states them here once.
///
/// They carry no namespace, and Redis pub/sub is instance-wide — a channel is not scoped by the database index.
/// Two deployments sharing one Redis must give these distinct names or they will hear each other.</remarks>
public sealed class WorkflowClusterSignalChannels
{
    /// <summary>Restart supersession: one replica cancels its runs of a workflow and tells the rest. Carries the
    /// workflow id, and the trigger's generation (<see cref="IWorkflowRestartGenerations"/>) as its
    /// payload: a peer cancels only its runs older than that. A cancel with no payload cancels every run. The word
    /// only makes a handover quick; a run that misses it finds the newer generation on its next look.</summary>
    public string RestartCancel { get; set; } = "workflow_restart_cancel";

    /// <summary>A debugger command issued for a session, for whichever replica holds the paused run. Carries the
    /// session id, and the command's name as its payload.</summary>
    public string DebugCommands { get; set; } = "workflow_debug_commands";

    /// <summary>The holder's confirmation that it applied a debugger command, for the replica that sent it. Carries
    /// the session id, and the applied command's name as its payload. A sender waits a bounded time for it and
    /// reports an unconfirmed command as such.</summary>
    public string DebugCommandAcks { get; set; } = "workflow_debug_command_acks";

    /// <summary>A breakpoint set replaced for an execution. Carries the execution log id, and the whole
    /// replacement set as a JSON array of node ids — never a delta, because a receiver that missed one message
    /// would apply the next to a set it does not have.</summary>
    public string DebugBreakpoints { get; set; } = "workflow_debug_breakpoints";
}

/// <summary>Who this process is on the wire, for the one job that cannot be done without it: telling a peer's
/// broadcast from an echo of your own.</summary>
/// <remarks>Registered by <c>AddWorkflows</c> from <c>Defaults.ReplicaId</c>. A host that named no replica gets a
/// per-process id instead of an empty one, because two processes that both call themselves "" hear each other as
/// themselves and drop every message that matters.</remarks>
public sealed class WorkflowClusterIdentity
{
    /// <summary>Takes the host's replica id, or invents one for this process when the host named none.</summary>
    public WorkflowClusterIdentity(string? replicaId = null) =>
        ReplicaId = string.IsNullOrWhiteSpace(replicaId) ? $"proc-{Guid.NewGuid():N}" : replicaId;

    /// <summary>Stable for the life of the process. Whether it differs from every peer's is the host's to get
    /// right when it names one — a machine name shared by two replicas is one id, and they stop hearing each
    /// other; only an id this class invented is unique by construction.</summary>
    public string ReplicaId { get; }
}
