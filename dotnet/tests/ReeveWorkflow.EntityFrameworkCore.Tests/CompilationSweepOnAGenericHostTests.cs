using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class CompilationSweepOnAGenericHostTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly string file = Path.Combine(Path.GetTempPath(), $"reeve-workflow-{Guid.NewGuid():N}.db");
    private readonly ConcurrentQueue<Logged> logged = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public void The_longest_interval_is_the_longest_delay_Task_Delay_accepts()
    {
        var clock = new TimerClock(DateTimeOffset.UtcNow);
        using var cancel = new CancellationTokenSource();

        Action longest = () => _ = Task.Delay(WorkflowCompilationSweepOptions.MaximumInterval, clock, cancel.Token);
        Action longer = () => _ = Task.Delay(WorkflowCompilationSweepOptions.MaximumInterval + TimeSpan.FromMilliseconds(1), clock, cancel.Token);

        longest.Should().NotThrow();
        longer.Should().Throw<ArgumentOutOfRangeException>();
        cancel.Cancel();
    }

    [Fact]
    public async Task A_wait_that_fails_ends_the_timer_with_an_error_and_the_host_keeps_running()
    {
        var timers = new FailingTimers();
        using var host = await StartedHostAsync(timers);

        await Service(host).ExecuteTask!.WaitAsync(Deadline);

        logged.Should().ContainSingle(l => l.Level >= LogLevel.Error).Which.Should().Match<Logged>(l =>
            l.Category == typeof(WorkflowCompilationSweepService).FullName && l.Error == timers.Thrown);
        Lifetime(host).ApplicationStopping.IsCancellationRequested.Should().BeFalse();
        await host.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task Stopping_the_host_while_the_timer_waits_logs_nothing_and_ends_the_service()
    {
        var timers = new RecordingTimers(TimeProvider.System);
        using var host = await StartedHostAsync(timers);
        (await timers.ArmedWith(TimeSpan.FromHours(6)).WaitAsync(Deadline)).Should().BeTrue();

        await host.StopAsync().WaitAsync(Deadline);

        Service(host).ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        logged.Should().NotContain(l => l.Level >= LogLevel.Warning && l.Category.StartsWith("ReeveWorkflow", StringComparison.Ordinal));
    }

    private async Task<IHost> StartedHostAsync(TimeProvider timeProvider)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Collecting(logged));
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite($"Data Source={file};Default Timeout=30;Pooling=False"));
            options.CompilationSweep.Enabled = true;
        });

        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        await host.StartAsync().WaitAsync(Deadline);
        return host;
    }

    private static WorkflowCompilationSweepService Service(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<WorkflowCompilationSweepService>().Single();

    private static IHostApplicationLifetime Lifetime(IHost host) => host.Services.GetRequiredService<IHostApplicationLifetime>();

    private sealed record Logged(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingTimers(TimeProvider inner) : TimeProvider
    {
        private readonly ConcurrentDictionary<TimeSpan, TaskCompletionSource<bool>> armed = new();

        public Task<bool> ArmedWith(TimeSpan dueTime) => Signal(dueTime).Task;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Signal(dueTime).TrySetResult(true);
            return timer;
        }

        private TaskCompletionSource<bool> Signal(TimeSpan dueTime) =>
            armed.GetOrAdd(dueTime, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class FailingTimers : TimeProvider
    {
        public InvalidOperationException Thrown { get; } = new("no timer can be armed");

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => throw Thrown;
    }

    private sealed class Collecting(ConcurrentQueue<Logged> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<Logged> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Logged(logLevel, category, formatter(state, exception), exception));
        }
    }
}
