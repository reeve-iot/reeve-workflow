using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>The questions Single and Restart ask of runs parked on a durable sleep. A suspension store opts in by
/// implementing this too; without it the engine never parks a sleep.</summary>
/// <remarks>Its own interface rather than members on <see cref="IWorkflowSuspensionStore"/>, which every host
/// that parks a run implements. Implemented by the same object, so both read the same table.</remarks>
public interface IWorkflowParkedSleepStore
{
    /// <summary>Whether any run of this workflow is parked on a sleep right now, as <see cref="ListParkedSleepsAsync"/>
    /// counts one. A Single run is skipped while one is.</summary>
    Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>This workflow's runs asleep on a sleep row: runs whose journal is still running and whose newest
    /// suspension row is a sleep, <c>Suspended</c>, or <c>Resumed</c> or <c>TimedOut</c> by a waker however long ago.</summary>
    /// <remarks>A taken row is listed because its resume takes the lease after the take. The engine keeps a taken row only
    /// while the run's state row is still <c>Suspended</c>, which the resume changes once it holds the lease.</remarks>
    Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Swaps one sleep row, parked or taken, to <c>Cancelled</c>. False when it had already moved on. The
    /// caller ends the sleep's run first, so a row is never cancelled under a run that nothing will end.</summary>
    Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken);

    /// <summary>Whether a run of this workflow other than the one named started after the instant given. Every resumed
    /// Restart sleep asks this: only a run started after it parked has superseded it.</summary>
    /// <remarks>Both instants are replica clocks, the newer run's start and the park, as no column is stamped by the database.
    /// Replicas whose clocks differ by more than the gap between a park and the next fire can misjudge it; keep them synced.</remarks>
    Task<bool> HasRunStartedSinceAsync(
        Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc, CancellationToken cancellationToken);
}

/// <summary>A run parked on a sleep: its row, its run, the node it sleeps on, and the payload naming its state row.</summary>
public sealed record WorkflowParkedSleep(
    Guid SuspensionId,
    Guid ExecutionLogId,
    string BlockId,
    WorkflowSuspensionStatus Status,
    string StatePayloadJson,
    DateTimeOffset SuspendedAtUtc);
