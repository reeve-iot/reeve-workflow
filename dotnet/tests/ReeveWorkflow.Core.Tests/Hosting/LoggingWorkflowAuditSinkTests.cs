using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.Core.Tests.Hosting;

public class LoggingWorkflowAuditSinkTests
{
    private sealed class CapturingLogger : ILogger<LoggingWorkflowAuditSink>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task An_event_reaches_the_log_with_its_kind_and_subject()
    {
        var logger = new CapturingLogger();
        var subjectId = Guid.NewGuid();

        await new LoggingWorkflowAuditSink(logger).WriteAsync(new WorkflowAuditEvent(
            WorkflowAuditEventKinds.ScriptingDenied, ActorId: null, SubjectId: subjectId, Data: new { Reason = "no" }));

        var line = logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain(WorkflowAuditEventKinds.ScriptingDenied);
        line.Should().Contain(subjectId.ToString());
        line.Should().Contain("no");
    }

    [Fact]
    public async Task Detail_the_serialiser_refuses_costs_the_detail_and_not_the_line()
    {
        var logger = new CapturingLogger();

        await new LoggingWorkflowAuditSink(logger).WriteAsync(new WorkflowAuditEvent(
            WorkflowAuditEventKinds.RunDropped, ActorId: null, SubjectId: Guid.NewGuid(), Data: IntPtr.Zero));

        var line = logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain(WorkflowAuditEventKinds.RunDropped);
        line.Should().Contain("unserialisable");
    }
}
