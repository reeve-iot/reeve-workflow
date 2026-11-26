using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public class ClusterSignalDebugSignalTests
{
    private static readonly Guid Session = Guid.Parse("2c3d4e5f-6071-8293-a4b5-c6d7e8f90123");

    [Theory]
    [InlineData(WorkflowDebugCommand.Step)]
    [InlineData(WorkflowDebugCommand.StepInto)]
    [InlineData(WorkflowDebugCommand.StepOver)]
    [InlineData(WorkflowDebugCommand.Resume)]
    [InlineData(WorkflowDebugCommand.Kill)]
    public void Every_command_survives_the_trip_to_a_peer(WorkflowDebugCommand command)
    {
        WorkflowDebugSignalPayload.TryReadCommand(WorkflowDebugSignalPayload.Write(command), out var read)
            .Should().BeTrue();
        read.Should().Be(command);
    }

    [Fact]
    public void A_command_is_written_by_name_so_a_new_member_cannot_shift_an_old_one()
    {
        WorkflowDebugSignalPayload.Write(WorkflowDebugCommand.Resume).Should().Be("Resume");
    }

    [Theory]
    [InlineData("Detonate")]
    [InlineData("")]
    [InlineData(null)]
    public void A_command_this_version_does_not_have_is_dropped_rather_than_guessed_at(string? payload)
    {
        WorkflowDebugSignalPayload.TryReadCommand(payload, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    public void A_command_written_as_a_number_is_not_read(string payload)
    {
        WorkflowDebugSignalPayload.TryReadCommand(payload, out _).Should().BeFalse();
    }

    [Fact]
    public void A_breakpoint_set_survives_ids_that_contain_the_characters_a_separator_would_use()
    {
        string[] ids = ["node,1", "node;2", "node\"3", "node 4"];

        WorkflowDebugSignalPayload.TryReadNodeIds(WorkflowDebugSignalPayload.Write(ids), out var read)
            .Should().BeTrue();
        read.Should().Equal(ids);
    }

    [Fact]
    public void An_empty_breakpoint_set_is_a_set_and_not_a_lost_message()
    {
        WorkflowDebugSignalPayload.TryReadNodeIds(WorkflowDebugSignalPayload.Write([]), out var read)
            .Should().BeTrue();
        read.Should().BeEmpty();
    }

    [Fact]
    public void A_breakpoint_set_with_a_hole_in_it_is_dropped_rather_than_half_applied()
    {
        WorkflowDebugSignalPayload.TryReadNodeIds("""["n1",null,"n3"]""", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("")]
    public void A_breakpoint_payload_that_does_not_parse_is_dropped_rather_than_applied_as_empty(string payload)
    {
        WorkflowDebugSignalPayload.TryReadNodeIds(payload, out _).Should().BeFalse();
    }

    [Fact]
    public void The_debuggers_signals_are_each_on_a_channel_of_their_own()
    {
        var transport = new RecordingSignal();
        using var provider = Build(transport: transport);

        _ = provider.GetRequiredService<WorkflowDebugSessionService>();

        transport.Subscribed.Should().HaveCount(3).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_channel_names_come_from_the_deployment_and_not_from_this_package()
    {
        var transport = new RecordingSignal();
        using var provider = Build(
            transport: transport,
            channels: c =>
            {
                c.DebugCommands = "desk_debug_commands";
                c.DebugCommandAcks = "desk_debug_command_acks";
            });

        _ = provider.GetRequiredService<WorkflowDebugSessionService>();

        transport.Subscribed.Should().Contain(["desk_debug_commands", "desk_debug_command_acks"]);
    }

    [Fact]
    public void A_deployment_with_no_transport_subscribes_to_nothing()
    {
        var transport = new RecordingSignal();
        using var provider = Build(transport: null);

        _ = provider.GetRequiredService<WorkflowDebugSessionService>();

        transport.Subscribed.Should().BeEmpty();
    }

    private static ServiceProvider Build(
        IWorkflowClusterSignal? transport,
        Action<WorkflowClusterSignalChannels>? channels = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(new SqliteConnection("Filename=:memory:")));
            channels?.Invoke(options.Defaults.SignalChannels);
            if (transport is not null) options.Seams.ClusterSignal.UseInstance(transport);
        });
        services.AddWorkflowDebug(seams: debug => debug.BlobStore.Use<UnusedBlobStore>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingSignal : IWorkflowClusterSignal
    {
        private readonly List<(string Channel, Func<Guid, string?, CancellationToken, Task> Handler)> handlers = [];

        public List<(string Channel, Guid Id, string? Payload)> Published { get; } = [];

        public Task PublishAsync(
            string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default)
        {
            Published.Add((channel, id, payload));
            return Task.CompletedTask;
        }

        public List<string> Subscribed { get; } = [];

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
        {
            Subscribed.Add(channel);
            handlers.Add((channel, handler));
            return new Unsubscribe(() => handlers.RemoveAll(h => h.Handler == handler));
        }

        public async Task DeliverAsync(string channel, Guid id, string? payload)
        {
            foreach (var subscribed in handlers.Where(h => h.Channel == channel).ToArray())
                await subscribed.Handler(id, payload, CancellationToken.None);
        }

        private sealed class Unsubscribe(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private sealed class UnusedBlobStore : IWorkflowDebugBlobStore
    {
        public Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
