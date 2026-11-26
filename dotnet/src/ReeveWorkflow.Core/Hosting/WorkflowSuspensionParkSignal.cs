using System;
using System.Threading;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>Signals this process's waker after a run has completed parking its suspension.</summary>
public sealed class WorkflowSuspensionParkSignal
{
    private event Action? Parked;

    /// <summary>Subscribes a process-local waker to successful parks and returns its subscription.</summary>
    public IDisposable Subscribe(Action onParked)
    {
        ArgumentNullException.ThrowIfNull(onParked);
        Parked += onParked;
        return new Subscription(this, onParked);
    }

    /// <summary>Wakes subscribers after a suspension has been parked successfully.</summary>
    public void Publish()
    {
        if (Parked is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception) { }
        }
    }

    private sealed class Subscription(WorkflowSuspensionParkSignal owner, Action callback) : IDisposable
    {
        private Action? callback = callback;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref callback, null);
            if (current is not null) owner.Parked -= current;
        }
    }
}
