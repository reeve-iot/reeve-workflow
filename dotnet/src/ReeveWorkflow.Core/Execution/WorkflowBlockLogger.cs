using System.Collections;
using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.Core.Execution;

internal sealed class WorkflowBlockLogger(ILogger inner, Guid executionId, string nodeId) : ILogger
{
    private readonly BlockScope scope = new(executionId, nodeId);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel))
            return;

        using (inner.BeginScope(scope))
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class BlockScope(Guid executionId, string nodeId) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private string? text;

        public int Count => 2;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new("WorkflowExecutionId", executionId),
            1 => new("WorkflowNodeId", nodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
            yield return this[1];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => text ??= $"WorkflowExecutionId:{executionId} WorkflowNodeId:{nodeId}";
    }
}
