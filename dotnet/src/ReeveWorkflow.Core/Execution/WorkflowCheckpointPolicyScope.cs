namespace ReeveWorkflow.Core.Execution;

/// <summary>Which <see cref="RunStateWriteMode"/> the run in this async flow writes its row under, when that is
/// the workflow's own rather than the engine's.</summary>
/// <remarks>0, and the shape <see cref="WorkflowVersionPinScope"/> already has. The runner and the
/// resumer read the workflow's <c>CheckpointPolicyJson</c> once, at the start of a run and at a resume, and push
/// it here; the walk reads it in place of its own options. Unset is the engine's mode, which is what every
/// workflow had by default. A sub-workflow pushes its own over its caller's, and gets its caller's back
/// on the way out — a child's policy is the child's.</remarks>
public static class WorkflowCheckpointPolicyScope
{
    private static readonly AsyncLocal<RunStateWriteMode?> _current = new();

    /// <summary>The mode the current run states, or null for the engine's.</summary>
    public static RunStateWriteMode? Current => _current.Value;

    /// <summary>Push a mode for the current async flow; dispose restores the previous value.</summary>
    public static IDisposable Push(RunStateWriteMode? mode)
    {
        var previous = _current.Value;
        _current.Value = mode;
        return new Restore(previous);
    }

    private sealed class Restore(RunStateWriteMode? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
