using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReeveWorkflow.Core.Hosting;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis.Tests;

public class RedisWorkflowClusterSignalTests
{
    private const string Channel = "workflow_restart_cancel";
    private static readonly Guid Subject = Guid.Parse("6f1a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8");

    private sealed class Replica
    {
        public required RedisWorkflowClusterSignal Signal { get; init; }
        public required ISubscriber Subscriber { get; init; }

        public List<RedisValue> Published { get; } = [];

        public Action<RedisChannel, RedisValue>? Deliver { get; set; }
    }

    private static Replica Build(
        string? replicaId = "replica-a", Exception? publishThrows = null, Exception? subscribeThrows = null)
    {
        var subscriber = Substitute.For<ISubscriber>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetSubscriber(Arg.Any<object>()).Returns(subscriber);

        var replica = new Replica
        {
            Signal = new RedisWorkflowClusterSignal(
                multiplexer,
                new WorkflowClusterIdentity(replicaId),
                NullLogger<RedisWorkflowClusterSignal>.Instance),
            Subscriber = subscriber,
        };

        subscriber
            .PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                if (publishThrows is not null) throw publishThrows;
                replica.Published.Add(call.ArgAt<RedisValue>(1));
                return Task.FromResult(1L);
            });

        subscriber
            .SubscribeAsync(
                Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                if (subscribeThrows is not null) throw subscribeThrows;
                replica.Deliver = call.ArgAt<Action<RedisChannel, RedisValue>>(1);
                return Task.CompletedTask;
            });

        subscriber
            .When(s => s.Subscribe(
                Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw subscribeThrows ?? new InvalidOperationException(
                "The synchronous Subscribe waits for an ack and throws on a blip; use SubscribeAsync."));

        return replica;
    }

    private static async Task<(Guid Id, string? Payload)?> Heard(
        Replica replica, string channel, RedisValue message)
    {
        var heard = new TaskCompletionSource<(Guid, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = replica.Signal.Subscribe(
            channel, (id, payload, _) => { heard.TrySetResult((id, payload)); return Task.CompletedTask; });

        replica.Deliver!(RedisChannel.Literal(channel), message);

        var finished = await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        return finished == heard.Task ? await heard.Task : null;
    }

    [Fact]
    public async Task A_replica_does_not_act_on_the_broadcast_it_sent_itself()
    {
        var replica = Build();
        await replica.Signal.PublishAsync(Channel, Subject);

        var heard = await Heard(replica, Channel, replica.Published.Single());

        heard.Should().BeNull();
    }

    [Fact]
    public async Task A_peers_broadcast_arrives_with_the_id_and_payload_it_was_sent_with()
    {
        var publisher = Build("replica-a");
        var listener = Build("replica-b");
        await publisher.Signal.PublishAsync(Channel, Subject, "Resume");

        var heard = await Heard(listener, Channel, publisher.Published.Single());

        heard.Should().Be((Subject, "Resume"));
    }

    [Fact]
    public async Task A_channel_that_carries_only_an_id_delivers_a_null_payload()
    {
        var publisher = Build("replica-a");
        var listener = Build("replica-b");
        await publisher.Signal.PublishAsync(Channel, Subject);

        var heard = await Heard(listener, Channel, publisher.Published.Single());

        heard.Should().Be((Subject, (string?)null));
    }

    [Fact]
    public async Task The_wire_field_names_are_written_out_because_a_peer_on_another_version_parses_them()
    {
        var replica = Build("replica-a");

        await replica.Signal.PublishAsync(Channel, Subject, "Step");

        using var document = JsonDocument.Parse(replica.Published.Single().ToString());
        document.RootElement.GetProperty("Origin").GetString().Should().Be("replica-a");
        document.RootElement.GetProperty("Id").GetString().Should().Be(Subject.ToString("N"));
        document.RootElement.GetProperty("Payload").GetString().Should().Be("Step");
    }

    [Fact]
    public async Task A_message_that_is_not_a_signal_is_dropped_rather_than_thrown()
    {
        var replica = Build("replica-b");

        var heard = await Heard(replica, Channel, "not json at all");

        heard.Should().BeNull();
    }

    [Fact]
    public async Task A_message_whose_id_is_not_a_guid_is_dropped()
    {
        var replica = Build("replica-b");
        var message = JsonSerializer.Serialize(new { Origin = "replica-a", Id = "seven", Payload = (string?)null });

        var heard = await Heard(replica, Channel, message);

        heard.Should().BeNull();
    }

    [Fact]
    public async Task A_publish_that_fails_does_not_reach_the_caller()
    {
        var replica = Build(publishThrows: new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var publishing = async () => await replica.Signal.PublishAsync(Channel, Subject);

        await publishing.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_cancelled_publish_is_a_cancellation_and_not_a_swallowed_fault()
    {
        var replica = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var publishing = async () => await replica.Signal.PublishAsync(Channel, Subject, null, cancelled.Token);

        await publishing.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_take_the_next_message_with_it()
    {
        var publisher = Build("replica-a");
        var listener = Build("replica-b");
        await publisher.Signal.PublishAsync(Channel, Subject);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;

        using var subscription = listener.Signal.Subscribe(Channel, (_, _, _) =>
        {
            if (first) { first = false; throw new InvalidOperationException("the handler is broken"); }
            second.TrySetResult();
            return Task.CompletedTask;
        });

        var message = publisher.Published.Single();
        listener.Deliver!(RedisChannel.Literal(Channel), message);
        listener.Deliver!(RedisChannel.Literal(Channel), message);

        (await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(2)))).Should().Be(second.Task);
    }

    [Fact]
    public void A_subscribe_that_cannot_reach_redis_does_not_reach_the_caller()
    {
        var replica = Build(subscribeThrows: new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var subscribing = () => replica.Signal.Subscribe(Channel, (_, _, _) => Task.CompletedTask);

        subscribing.Should().NotThrow();
    }

    [Fact]
    public void A_subscription_that_never_reached_redis_can_still_be_disposed()
    {
        var replica = Build(subscribeThrows: new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var subscription = replica.Signal.Subscribe(Channel, (_, _, _) => Task.CompletedTask);
        subscription.Dispose();

        replica.Subscriber.Received(1).UnsubscribeAsync(
            Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Disposing_a_subscription_unsubscribes_once_however_often_it_is_disposed()
    {
        var replica = Build();
        var subscription = replica.Signal.Subscribe(Channel, (_, _, _) => Task.CompletedTask);

        subscription.Dispose();
        subscription.Dispose();

        replica.Subscriber.Received(1).UnsubscribeAsync(
            Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Two_processes_that_were_given_no_replica_id_are_still_two_origins()
    {
        var publisher = Build(replicaId: null);
        var listener = Build(replicaId: null);
        await publisher.Signal.PublishAsync(Channel, Subject);

        var heard = await Heard(listener, Channel, publisher.Published.Single());

        heard.Should().Be((Subject, (string?)null));
    }

    [Fact]
    public async Task A_process_that_was_given_no_replica_id_still_does_not_hear_itself()
    {
        var replica = Build(replicaId: null);
        await replica.Signal.PublishAsync(Channel, Subject);

        var heard = await Heard(replica, Channel, replica.Published.Single());

        heard.Should().BeNull();
    }
}
