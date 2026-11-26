namespace ReeveWorkflow.Core.Execution;

/// <summary>The call depth of the run walking on this async flow: how many runs called down to it. Pushed when a
/// run or a resumed segment starts; read by whatever starts a sub-workflow, which hands its child one more.</summary>
/// <remarks>A block's own <see cref="AsyncLocal{T}"/> chain was the only bound on workflow-to-workflow
/// recursion before, and it started empty again in every segment that resumed after a wait.</remarks>
public static class WorkflowCallDepthScope
{
    private static readonly AsyncLocal<int> _current = new();

    /// <summary>The depth of the run on this flow; 0 outside any run.</summary>
    public static int Current => _current.Value;

    /// <summary>Sets the depth for the current async flow; disposing restores what was there before.</summary>
    public static IDisposable Push(int depth)
    {
        var previous = _current.Value;
        _current.Value = depth;
        return new Restore(previous);
    }

    private sealed class Restore(int previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
