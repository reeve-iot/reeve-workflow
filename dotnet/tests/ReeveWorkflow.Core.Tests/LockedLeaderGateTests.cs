using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.Core.Tests;

public class LockedLeaderGateTests
{
    [Fact]
    public async Task The_replica_that_takes_the_lock_runs_the_sweep()
    {
        var locks = new CountingLocks(granted: true);
        var ran = 0;

        await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-retention", _ => { ran++; return Task.CompletedTask; }, CancellationToken.None);

        ran.Should().Be(1);
    }

    [Fact]
    public async Task A_replica_that_does_not_get_the_lock_does_not_run_it()
    {
        var locks = new CountingLocks(granted: false);
        var ran = 0;

        await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-retention", _ => { ran++; return Task.CompletedTask; }, CancellationToken.None);

        ran.Should().Be(0);
    }

    [Fact]
    public async Task The_lock_is_given_back_when_the_sweep_finishes()
    {
        var locks = new CountingLocks(granted: true);

        await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-retention", _ => Task.CompletedTask, CancellationToken.None);

        locks.Released.Should().Be(1);
    }

    [Fact]
    public async Task The_lock_is_given_back_when_the_sweep_throws()
    {
        var locks = new CountingLocks(granted: true);

        var sweeping = async () => await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-retention", _ => throw new InvalidOperationException("the delete failed"), CancellationToken.None);

        await sweeping.Should().ThrowAsync<InvalidOperationException>();
        locks.Released.Should().Be(1, "a sweep that fails must not take leadership down with it");
    }

    [Fact]
    public async Task The_key_the_caller_names_is_the_key_the_lock_is_taken_on()
    {
        var locks = new CountingLocks(granted: true);

        await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-debug-retention", _ => Task.CompletedTask, CancellationToken.None);

        locks.Keys.Should().ContainSingle().Which.Should().Be("workflow-debug-retention");
    }

    [Fact]
    public async Task The_sweep_is_cancelled_with_the_token_the_host_is_stopping_on()
    {
        var locks = new CountingLocks(granted: true);
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        await new LockedLeaderGate(locks).RunUnderLeaderAsync(
            "workflow-retention", token => { seen = token; return Task.CompletedTask; }, cts.Token);

        seen.Should().Be(cts.Token);
    }

    [Fact]
    public void A_gate_without_a_lock_provider_is_refused_where_it_is_built()
    {
        var building = () => new LockedLeaderGate(null!);

        building.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_tick_this_replica_did_not_lead_says_so_rather_than_logging_nothing()
    {
        var locks = new CountingLocks(granted: false);
        var log = new CapturingLogger();

        await new LockedLeaderGate(locks, log).RunUnderLeaderAsync(
            "workflow-retention", _ => Task.CompletedTask, CancellationToken.None);

        log.Messages.Should().ContainSingle().Which.Should().Contain("workflow-retention");
    }

    private sealed class CapturingLogger : ILogger<LockedLeaderGate>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class CountingLocks(bool granted) : IWorkflowRunLockProvider
    {
        public List<string> Keys { get; } = [];

        public int Released { get; private set; }

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            Keys.Add(lockKey);
            return Task.FromResult<IWorkflowRunLock?>(granted ? new Lease(() => Released++) : null);
        }

        private sealed class Lease(Action onRelease) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync()
            {
                onRelease();
                return ValueTask.CompletedTask;
            }
        }
    }
}
