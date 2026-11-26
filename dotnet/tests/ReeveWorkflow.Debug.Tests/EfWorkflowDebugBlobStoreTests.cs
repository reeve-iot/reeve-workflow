using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public class EfWorkflowDebugBlobStoreTests : IDisposable
{
    private readonly SqliteConnection connection = new("Filename=:memory:");
    private readonly ServiceProvider provider;

    public EfWorkflowDebugBlobStoreTests()
    {
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connection));
        });
        services.AddWorkflowDebug();

        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        provider.Dispose();
        connection.Dispose();
    }

    [Fact]
    public void A_host_that_installs_the_package_and_says_nothing_gets_somewhere_to_put_them()
    {
        provider.GetRequiredService<IWorkflowDebugBlobStore>().Should().BeOfType<EfWorkflowDebugBlobStore>();
    }

    [Fact]
    public async Task What_was_put_under_a_key_is_what_comes_back()
    {
        var store = Store();

        await store.PutBytesAsync("session-1/snapshots/node-4", Bytes("captured"));

        (await store.GetBytesAsync("session-1/snapshots/node-4")).Should().Equal(Bytes("captured"));
    }

    [Fact]
    public async Task A_re_captured_snapshot_replaces_its_predecessor_rather_than_accumulating()
    {
        var store = Store();
        await store.PutBytesAsync("session-1", Bytes("first"));

        await store.PutBytesAsync("session-1", Bytes("second"));

        (await store.GetBytesAsync("session-1")).Should().Equal(Bytes("second"));
    }

    [Fact]
    public async Task A_key_nobody_wrote_reads_as_absent_rather_than_throwing()
    {
        (await Store().GetBytesAsync("session-1/snapshots/gone")).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_key_nobody_holds_is_not_an_error()
    {
        var delete = () => Store().DeleteAsync("session-1/snapshots/gone");

        await delete.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_deleted_blob_is_gone()
    {
        var store = Store();
        await store.PutBytesAsync("session-1", Bytes("captured"));

        await store.DeleteAsync("session-1");

        (await store.GetBytesAsync("session-1")).Should().BeNull();
    }

    [Fact]
    public async Task Another_replica_reads_what_this_one_wrote()
    {
        await Store().PutBytesAsync("session-1", Bytes("captured"));

        (await Store().GetBytesAsync("session-1")).Should().Equal(Bytes("captured"));
    }

    private IWorkflowDebugBlobStore Store() =>
        new EfWorkflowDebugBlobStore(provider.GetRequiredService<IServiceScopeFactory>());

    private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);
}
