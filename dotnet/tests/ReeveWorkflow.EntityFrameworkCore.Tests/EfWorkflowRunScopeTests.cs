using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class EfWorkflowRunScopeTests
{
    private sealed class WatchedScope(IServiceScope inner) : IServiceScope
    {
        public bool Disposed { get; private set; }

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    private sealed class RefusingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RefusingScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new RefusingProvider();

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_run_scope_carries_the_four_things_a_run_needs()
    {
        using var database = new SqliteWorkflowDatabase();

        using var runScope = database.Services.GetRequiredService<IWorkflowRunScopeFactory>().CreateScope();

        runScope.Store.Should().BeOfType<EfWorkflowStore>();
        runScope.Journal.Should().BeOfType<EfWorkflowRunJournal>();
        runScope.Audit.Should().NotBeNull();
        runScope.Authorization.Should().NotBeNull();
        runScope.Services.Should().NotBeNull();
    }

    [Fact]
    public void Disposing_the_run_scope_disposes_the_container_scope_under_it()
    {
        using var database = new SqliteWorkflowDatabase();
        var watched = new WatchedScope(
            database.Services.GetRequiredService<IServiceScopeFactory>().CreateScope());

        new EfWorkflowRunScope(watched).Dispose();

        watched.Disposed.Should().BeTrue();
    }

    [Fact]
    public void A_scope_whose_contents_will_not_resolve_is_disposed_rather_than_leaked()
    {
        var refusing = new RefusingScope();

        var act = () => new EfWorkflowRunScope(refusing);

        act.Should().Throw<InvalidOperationException>();
        refusing.Disposed.Should().BeTrue();
    }
}
