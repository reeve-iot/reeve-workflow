using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.Core.Tests.Hosting;

public class ConfiguredWorkflowAuthorizationTests
{
    private sealed class CountingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task An_empty_grant_set_denies_every_capability()
    {
        var authorization = new ConfiguredWorkflowAuthorization([], NullLogger.Instance);

        var granted = await authorization.HasCapabilityAsync(Guid.NewGuid(), WorkflowCapabilities.Scripting);

        granted.Should().BeFalse();
    }

    [Fact]
    public async Task A_granted_capability_is_granted_to_every_principal()
    {
        var authorization = new ConfiguredWorkflowAuthorization(
            [WorkflowCapabilities.Scripting], NullLogger.Instance);

        (await authorization.HasCapabilityAsync(Guid.NewGuid(), WorkflowCapabilities.Scripting))
            .Should().BeTrue();
        (await authorization.HasCapabilityAsync(Guid.NewGuid(), WorkflowCapabilities.Scripting))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_capability_outside_the_set_is_denied_even_when_others_are_granted()
    {
        var authorization = new ConfiguredWorkflowAuthorization(
            [WorkflowCapabilities.Scripting], NullLogger.Instance);

        (await authorization.HasCapabilityAsync(Guid.NewGuid(), "workflow.something.else"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task The_first_denial_of_a_capability_says_so_and_the_rest_are_quiet()
    {
        var logger = new CountingLogger();
        var authorization = new ConfiguredWorkflowAuthorization([], logger);

        await authorization.HasCapabilityAsync(Guid.NewGuid(), WorkflowCapabilities.Scripting);
        await authorization.HasCapabilityAsync(Guid.NewGuid(), WorkflowCapabilities.Scripting);
        await authorization.HasCapabilityAsync(Guid.NewGuid(), "workflow.other");

        logger.Warnings.Should().HaveCount(2);
        logger.Warnings[0].Should().Contain(WorkflowCapabilities.Scripting);
        logger.Warnings[1].Should().Contain("workflow.other");
    }

    [Fact]
    public void The_granted_set_is_readable_without_asking_a_question()
    {
        var authorization = new ConfiguredWorkflowAuthorization(
            [WorkflowCapabilities.Scripting], NullLogger.Instance);

        authorization.GrantedCapabilities.Should().BeEquivalentTo([WorkflowCapabilities.Scripting]);
    }
}
