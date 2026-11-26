using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>Writes each audit event to the log rather than to a table. The default for a host with no audit
/// store of its own: an operator can still find out that a run was dropped or a scripting block refused.</summary>
/// <remarks>It is a default, not a substitute for an audit log — log retention is usually shorter than an audit
/// trail's and a log line is not queryable by subject. A host that must answer "what happened to this workflow"
/// months later registers its own <see cref="IWorkflowAuditSink"/>, which then takes this one's place.</remarks>
public sealed class LoggingWorkflowAuditSink(ILogger<LoggingWorkflowAuditSink> logger) : IWorkflowAuditSink
{
    private readonly ILogger<LoggingWorkflowAuditSink> logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        logger.LogInformation(
            "Workflow audit {Kind}: subject {SubjectId}, actor {ActorId}, data {Data}",
            auditEvent.Kind,
            auditEvent.SubjectId,
            auditEvent.ActorId,
            Describe(auditEvent.Data));

        return Task.CompletedTask;
    }

    private static string Describe(object? data)
    {
        if (data is null) return "none";

        try
        {
            return JsonSerializer.Serialize(data);
        }
        catch (NotSupportedException)
        {
            return $"<unserialisable {data.GetType().Name}>";
        }
        catch (JsonException)
        {
            return $"<unserialisable {data.GetType().Name}>";
        }
    }
}
