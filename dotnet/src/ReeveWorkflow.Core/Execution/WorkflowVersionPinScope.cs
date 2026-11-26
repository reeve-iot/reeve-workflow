using System.Text.Json;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Carries the active run's version manifest ({workflowId → versionId}) across the engine call stack
/// via <see cref="AsyncLocal{T}"/>. Pushed at run/resume start; read by SubWorkflowBlock and BuildSuspensionStatePayload.</summary>
public static class WorkflowVersionPinScope
{
    private static readonly AsyncLocal<IReadOnlyDictionary<Guid, Guid>?> _current = new();

    public static IReadOnlyDictionary<Guid, Guid>? Current => _current.Value;

    /// <summary>Serialised current manifest, or null when no run scope is active.</summary>
    public static string? CurrentJson()
        => _current.Value == null ? null : JsonSerializer.Serialize(_current.Value, WorkflowGraphHelpers.JsonOptions);

    /// <summary>Push a manifest for the current async flow; dispose restores the previous value.</summary>
    public static IDisposable Push(IReadOnlyDictionary<Guid, Guid> manifest)
    {
        var previous = _current.Value;
        _current.Value = manifest;
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlyDictionary<Guid, Guid>? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
