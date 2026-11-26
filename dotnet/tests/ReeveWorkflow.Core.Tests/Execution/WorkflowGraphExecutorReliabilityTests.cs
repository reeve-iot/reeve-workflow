using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorReliabilityTests
{
    private const string Wait = "test.wait";
    private const string Loop = "test.loop";
    private const string TryCatch = "test.trycatch";
    private const string Slow = "test.slow";
    private const string Counted = "test.counted";
    private const string Deadline = "test.deadline";
    private const string SlowContainer = "test.slowcontainer";
    private const string MediumContainer = "test.mediumcontainer";
    private const string Medium = "test.medium";
    private const string Stuck = "test.stuck";
    private const string Twice = "test.twice";

    [Fact]
    public async Task B6_A_wait_inside_a_loop_body_is_not_reported_as_a_block_failure()
    {
        var world = new World();

        var run = () => world.Run(Root(Container(Loop, Body(Wait))));

        var thrown = await run.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<WorkflowNodeExecutionException>(
            "a wait is not an error of the loop node; it is either parked or refused by name");
        thrown.Which.Message.Should().MatchRegex("(?i)sub-canvas|not supported|inside",
            "the run must say what happened: a wait where the engine cannot park");
    }

    [Fact]
    public async Task B6_A_wait_inside_a_try_body_is_not_swallowed_by_the_catch_branch()
    {
        var world = new World();

        var run = () => world.Run(Root(Container(TryCatch, Body(Wait))));

        await run.Should().ThrowAsync<Exception>(
            "a run that reports success after treating a wait as a caught error has lost the wait");
        world.Caught.Should().BeEmpty("the catch branch must never see a wait as an error");
    }

    [Fact]
    public async Task Flaw_i_A_host_that_asks_for_the_rule_by_name_does_not_repeat_a_block_that_says_it_cannot_be()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0)
        };

        var run = () => world.Run(
            Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }),
            retryOnlyIdempotentBlocks: true);

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(1,
            "a block whose factory says it cannot be repeated must not be retried after a timeout");
    }

    [Fact]
    public async Task Flaw_i_A_host_that_says_nothing_no_longer_repeats_a_block_that_says_it_cannot_be()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(1, "nothing was said, and the block says it cannot be repeated");
    }

    [Fact]
    public async Task Flaw_i_A_host_can_still_ask_for_the_retries_it_had_before_five_point_zero()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0)
        };

        var run = () => world.Run(
            Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }),
            retryOnlyIdempotentBlocks: false);

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(4, "the host asked for the old behaviour by name");
    }

    [Fact]
    public async Task Flaw_i_A_node_that_asks_for_retries_gets_them_even_when_its_block_is_not_idempotent()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(true)
        };

        var run = () => world.Run(
            Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }),
            retryOnlyIdempotentBlocks: true);

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(4, "the node's own word beats the factory's default");
    }

    [Fact]
    public void Flaw_i_The_shipped_default_is_the_rule_on_both_objects_a_host_can_build()
    {
        new WorkflowGraphExecutorOptions().RetryOnlyIdempotentBlocks.Should().BeTrue();
        new WorkflowEngineSettings().RetryOnlyIdempotentBlocks.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Flaw_i_The_walk_runs_on_the_rule_the_deployments_settings_state(bool asked)
    {
        var settings = new WorkflowEngineSettings { RetryOnlyIdempotentBlocks = asked };

        WorkflowGraphExecutorOptions.For(settings).RetryOnlyIdempotentBlocks.Should().Be(asked);
    }

    [Fact]
    public void Flaw_i_Everything_the_walk_reads_comes_across_from_the_settings_it_was_composed_from()
    {
        var zone = TimeZoneInfo.Utc;
        var settings = new WorkflowEngineSettings
        {
            MaxPayloadValueBytes = 4096,
            TimeZone = zone,
            ResolveTimeZone = (_, _) => Task.FromResult<TimeZoneInfo?>(zone),
            DurableSleepBlockTypeIds = ["host.wait"],
            RetryOnlyIdempotentBlocks = false,
            DefaultContainerTimeoutSeconds = 90
        };

        var walk = WorkflowGraphExecutorOptions.For(settings);

        walk.MaxPayloadValueBytes.Should().Be(4096);
        walk.TimeZone.Should().Be(zone);
        walk.ResolveTimeZone.Should().BeSameAs(settings.ResolveTimeZone);
        walk.DurableSleepBlockTypeIds.Should().Contain("host.wait");
        walk.RetryOnlyIdempotentBlocks.Should().BeFalse();
        walk.DefaultContainerTimeoutSeconds.Should().Be(90);
    }

    [Fact]
    public async Task Flaw_i_A_count_an_older_editor_wrote_is_not_the_author_asking_for_retries()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0),
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(1, "the block says it cannot be repeated and nobody decided otherwise");
    }

    [Fact]
    public async Task Flaw_i_A_stored_count_the_engine_does_not_use_is_said_out_loud()
    {
        var logs = new CollectingLoggerFactory();
        var world = new World { Loggers = logs };
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        var said = world.LogEntries.Where(e => e.Level == LogEntryLevel.Warning).Should().ContainSingle().Subject;
        said.NodeId.Should().Be("slow");
        said.Message.Should().Contain("5").And.Contain("retryEnabled");
        var logged = logs.Warnings.Should().ContainSingle().Subject;
        logged.Message.Should().Contain("slow").And.Contain("5");
        logged.Category.Should().Be("ReeveWorkflow.Definition",
            "a category carrying a run's id is one no operator can write a filter rule for, and silencing "
            + "a block's own logging to quiet this is not a workaround");
    }

    [Fact]
    public async Task Flaw_i_A_stored_count_on_a_node_the_run_enters_twice_is_said_once()
    {
        var logs = new CollectingLoggerFactory();
        var world = new World { Loggers = logs };
        var config = new Dictionary<string, JsonElement>
        {
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        await world.Run(Root(Container(Twice, Body(
            new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }))));

        world.CountedExecutions.Should().Be(2, "the body did run twice, which is what makes this a repeat");
        world.LogEntries.Where(e => e.Level == LogEntryLevel.Warning).Should().ContainSingle();
        logs.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task Flaw_i_Saying_it_once_is_once_per_node_and_not_once_per_run()
    {
        var logs = new CollectingLoggerFactory();
        var world = new World { Loggers = logs };
        var config = new Dictionary<string, JsonElement>
        {
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        await world.Run(new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "first", BlockTypeId = Counted, Config = config },
                new WorkflowNodeDefinition { Id = "second", BlockTypeId = Counted, Config = config }
            ],
            Edges =
            [
                new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "first" },
                new WorkflowConnectionDefinition { Id = "e2", SourceNodeId = "first", TargetNodeId = "second" }
            ]
        });

        logs.Warnings.Should().HaveCount(2, "two nodes stored a count nothing reads, and both authors are owed a word");
        logs.Warnings.Select(w => w.Message).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Flaw_i_A_node_whose_author_said_no_is_not_warned_about_its_own_decision()
    {
        var logs = new CollectingLoggerFactory();
        var world = new World { Loggers = logs };
        var config = new Dictionary<string, JsonElement>
        {
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false),
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        await world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        world.LogEntries.Where(e => e.Level == LogEntryLevel.Warning).Should().BeEmpty();
        logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Flaw_i_A_node_that_asks_for_retries_gets_the_count_it_stored()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(true),
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(6, "five retries after the first attempt");
    }

    [Fact]
    public async Task Flaw_i_A_node_that_turned_retries_off_keeps_them_off_whatever_count_it_carries()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false),
            ["retryCount"] = JsonSerializer.SerializeToElement(5)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(1, "the node said no, and a count is not a contradiction of that");
    }

    [Fact]
    public async Task A_container_body_runs_on_the_run_s_own_deadline_not_a_thirty_second_one()
    {
        var world = new World();

        await world.Run(Root(Container(Deadline, Body(Counted))));

        world.ContainerSawADeadline.Should().BeFalse(
            "the engine imposed a deadline of its own on a container it cannot size");
    }

    [Fact]
    public async Task A_container_with_a_timeout_of_its_own_is_still_held_to_it()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
        };
        var node = Container(SlowContainer, Body(Counted));
        var timed = new WorkflowNodeDefinition
        {
            Id = node.Id, BlockTypeId = node.BlockTypeId, SubCanvases = node.SubCanvases, Config = config
        };

        var run = () => world.Run(Root(timed));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>(
            "the container was given a second and took longer");
    }

    [Fact]
    public async Task A_host_can_put_a_ceiling_on_containers_that_name_none_of_their_own()
    {
        var world = new World();

        var run = () => world.Run(Root(WithConfig(Container(SlowContainer, Body(Counted)), RetriesOff)),
            defaultContainerTimeoutSeconds: 1);

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>(
            "the host asked for a one-second ceiling and the container took longer");
    }

    [Fact]
    public async Task A_number_on_the_container_still_beats_the_host_s_ceiling()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(10),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
        };

        var run = () => world.Run(Root(WithConfig(Container(MediumContainer, Body(Counted)), config)),
            defaultContainerTimeoutSeconds: 1);

        await run.Should().NotThrowAsync("the node's ten seconds outrank the host's one");
    }

    [Fact]
    public async Task A_host_ceiling_leaves_leaf_blocks_on_their_own_default()
    {
        var world = new World();
        var node = new WorkflowNodeDefinition { Id = "medium", BlockTypeId = Medium, Config = RetriesOff };

        var run = () => world.Run(Root(node), defaultContainerTimeoutSeconds: 1);

        await run.Should().NotThrowAsync("a leaf keeps its own thirty seconds; the host's number is for containers");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_ceiling_of_nothing_is_refused_rather_than_quietly_becoming_a_second(int seconds)
    {
        var build = () => new WorkflowGraphExecutorOptions { DefaultContainerTimeoutSeconds = seconds };

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*null*");
    }

    [Fact]
    public void A_ceiling_longer_than_the_walk_can_apply_is_refused_where_the_host_sets_it()
    {
        var build = () => new WorkflowGraphExecutorOptions { DefaultContainerTimeoutSeconds = 172_800 };

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*86400*");
    }

    [Fact]
    public async Task A_timeout_of_zero_is_quietly_raised_to_a_second_rather_than_meaning_no_deadline()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(0),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "stuck", BlockTypeId = Stuck, Config = config }))
            .WaitAsync(TimeSpan.FromSeconds(30));

        var thrown = await run.Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeOfType<WorkflowNodeExecutionException>().Which.InnerException
            .Should().BeOfType<TimeoutRejectedException>("a clamp that was removed fails in Polly's validation instead")
            .Which.Timeout.Should().Be(TimeSpan.FromSeconds(1), "the author's 0 became one second");
    }

    [Fact]
    public async Task A_negative_retry_count_is_read_as_no_retries_at_all()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(true),
            ["retryCount"] = JsonSerializer.SerializeToElement(-5),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        world.SlowExecutions.Should().Be(1, "a count below one is no retries, even where the node asked for them");
    }

    [Theory]
    [InlineData(86400)]
    [InlineData(1)]
    public async Task A_deadline_the_walk_can_apply_is_applied(int seconds)
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(seconds),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        await run.Should().NotThrowAsync();
        world.CountedExecutions.Should().Be(1);
    }

    [Fact]
    public async Task A_deadline_longer_than_the_walk_can_apply_is_refused_by_name()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(604800),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        var thrown = await run.Should().ThrowAsync<InvalidWorkflowDefinitionException>();
        thrown.Which.Message.Should().Contain("timeoutSeconds").And.Contain("86400");
        world.CountedExecutions.Should().Be(0, "the node never runs, so the message is all the author gets");
    }

    [Fact]
    public async Task A_retry_delay_longer_than_the_walk_can_apply_is_refused_the_same_way()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(true),
            ["retryCount"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(90000)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        var thrown = await run.Should().ThrowAsync<InvalidWorkflowDefinitionException>();
        thrown.Which.Message.Should().Contain("retryBackoffSeconds").And.Contain("86400");
    }

    [Fact]
    public async Task A_delay_a_node_stores_but_never_uses_is_not_refused()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["retryEnabled"] = JsonSerializer.SerializeToElement(false),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(90000)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        await run.Should().NotThrowAsync();
        world.CountedExecutions.Should().Be(1);
    }

    [Fact]
    public async Task A_negative_backoff_is_quietly_raised_to_zero_rather_than_refused()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(1),
            ["retryEnabled"] = JsonSerializer.SerializeToElement(true),
            ["retryCount"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(-1)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "slow", BlockTypeId = Slow, Config = config }));

        var thrown = await run.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<InvalidWorkflowDefinitionException>(
            "a negative delay is taken and rewritten, not reported");
        world.SlowExecutions.Should().Be(2, "the one retry it was told to do still runs");
    }

    private static readonly Dictionary<string, JsonElement> RetriesOff = new()
    {
        ["retryEnabled"] = JsonSerializer.SerializeToElement(false)
    };

    private static WorkflowNodeDefinition WithConfig(
        WorkflowNodeDefinition node, IReadOnlyDictionary<string, JsonElement> config) => new()
    {
        Id = node.Id, BlockTypeId = node.BlockTypeId, SubCanvases = node.SubCanvases,
        Config = config.ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    [Fact]
    public async Task An_explicit_null_resilience_setting_is_absence_on_every_one_of_them()
    {
        var world = new World();
        var config = new Dictionary<string, JsonElement>
        {
            ["retryEnabled"] = JsonSerializer.SerializeToElement((object?)null),
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement((object?)null),
            ["retryCount"] = JsonSerializer.SerializeToElement((object?)null),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement((object?)null)
        };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted, Config = config }));

        await run.Should().NotThrowAsync();
        world.CountedExecutions.Should().Be(1, "the node runs on its defaults, as it does when the keys are absent");
    }

    [Fact]
    public async Task B7_A_run_whose_row_a_peer_has_taken_does_not_enter_the_next_block()
    {
        var world = new World { RowIsSomebodyElses = true };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted }), trackState: true);

        await run.Should().ThrowAsync<WorkflowRunOwnershipLostException>(
            "a walk on a row that is no longer this replica's must stop, and say by name why");
        world.CountedExecutions.Should().Be(0, "the block would otherwise run twice, once on each replica");
    }

    [Fact]
    public async Task B7_The_walk_stops_with_the_reason_the_row_gives_not_with_a_peer_nobody_saw()
    {
        var cancelledHere = new WorkflowRunStateRow
        {
            ExecutionId = Guid.NewGuid(), WorkflowId = Guid.NewGuid(),
            Status = WorkflowRunStateStatus.Cancelled, OwnedByReplicaId = "replica-1"
        };
        var world = new World { RowIsSomebodyElses = true, RowReadBack = cancelledHere };

        var run = () => world.Run(Root(new WorkflowNodeDefinition { Id = "c", BlockTypeId = Counted }), trackState: true);

        var thrown = await run.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.StatusMoved);
        thrown.Which.Message.Should().NotContain("another replica");
        world.CountedExecutions.Should().Be(0);
    }

    private static WorkflowDefinition Root(WorkflowNodeDefinition node) => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            node
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = node.Id }]
    };

    private static WorkflowNodeDefinition Container(string blockTypeId, WorkflowDefinition body) => new()
    {
        Id = "container",
        BlockTypeId = blockTypeId,
        SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body }
    };

    private static WorkflowDefinition Body(string blockTypeId) =>
        Body(new WorkflowNodeDefinition { Id = "inner", BlockTypeId = blockTypeId });

    private static WorkflowDefinition Body(WorkflowNodeDefinition node) => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            node
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e", SourceNodeId = "in", TargetNodeId = node.Id }]
    };

    private sealed class World
    {
        public int SlowExecutions;
        public int CountedExecutions;
        public bool ContainerSawADeadline;
        public List<SubCanvasError> Caught { get; } = [];
        public bool RowIsSomebodyElses { get; init; }

        public WorkflowRunStateRow? RowReadBack { get; init; }

        public List<WorkflowExecutionLogEntry> LogEntries { get; } = [];

        public ILoggerFactory Loggers { get; init; } = NullLoggerFactory.Instance;

        public Task Run(WorkflowDefinition definition, bool trackState = false,
            bool? retryOnlyIdempotentBlocks = null, int? defaultContainerTimeoutSeconds = null)
        {
            var recorder = trackState
                ? new WorkflowTransitionRecorder(
                    new FencedRunState(stillOurs: !RowIsSomebodyElses, RowReadBack), NullLogger.Instance,
                    new WorkflowTransitionOptions { ReplicaId = "replica-1" })
                : null;

            var options = retryOnlyIdempotentBlocks is { } asked
                ? new WorkflowGraphExecutorOptions
                {
                    RetryOnlyIdempotentBlocks = asked,
                    DefaultContainerTimeoutSeconds = defaultContainerTimeoutSeconds
                }
                : new WorkflowGraphExecutorOptions
                {
                    DefaultContainerTimeoutSeconds = defaultContainerTimeoutSeconds
                };

            var executor = new WorkflowGraphExecutor(
                new Registry(this), Loggers, new SilentMetrics(), new SilentObserver(),
                NullHelperResolver.Instance, options, transitions: recorder);

            return executor.ExecuteGraphAsync(
                [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
                LogEntries, [], WorkflowTraceMode.Full, debugMode: false, iterationIndex: null, callStackJson: null,
                depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
                triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                stateExecutionId: trackState ? Guid.NewGuid() : null,
                compiled: TestPrograms.Walk(definition, new Registry(this)));
        }
    }

    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, string Message)> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Collecting(categoryName, Warnings);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Collecting(string category, List<(string, string)> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Add((category, formatter(state, exception)));
            }
        }
    }

    private sealed class FencedRunState(bool stillOurs, WorkflowRunStateRow? readBack = null) : IWorkflowRunStateStore
    {
        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult(readBack);

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
            Task.FromResult(stillOurs);

        public Task<bool> CompleteAsync(Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Registry(World world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            Wait => new Factory(Wait, () => new WaitBlock()),
            Loop => new Factory(Loop, () => new LoopBlock(), subCanvases: ["Body"]),
            Twice => new Factory(Twice, () => new TwiceBlock(), subCanvases: ["Body"]),
            TryCatch => new Factory(TryCatch, () => new TryCatchBlock(world), subCanvases: ["Body"]),
            Slow => new Factory(Slow, () => new SlowBlock(world)),
            Deadline => new Factory(Deadline, () => new DeadlineBlock(world), subCanvases: ["Body"]),
            SlowContainer => new Factory(SlowContainer, () => new SlowContainerBlock(), subCanvases: ["Body"]),
            MediumContainer => new Factory(MediumContainer, () => new MediumContainerBlock(), subCanvases: ["Body"]),
            Medium => new Factory(Medium, () => new MediumBlock()),
            Stuck => new Factory(Stuck, () => new StuckBlock()),
            Counted => new Factory(Counted, () => new CountedBlock(world)),
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(string id, Func<IWorkflowBlockPlugin> create, IReadOnlyCollection<string>? subCanvases = null)
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => subCanvases ?? [];
        public bool IsIdempotent => false;
        public IWorkflowBlockPlugin Create() => create();
    }

    private sealed class WaitBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private sealed class TwiceBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await context.SubCanvasExecutor!.ExecuteAsync("Body", inputs, cancellationToken);
            await context.SubCanvasExecutor!.ExecuteAsync("Body", inputs, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class LoopBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await context.SubCanvasExecutor!.ExecuteAsync("Body", inputs, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class DeadlineBlock(World world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.ContainerSawADeadline = cancellationToken.CanBeCanceled;
            await context.SubCanvasExecutor!.ExecuteAsync("Body", inputs, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class MediumBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class StuckBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class MediumContainerBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class SlowContainerBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class TryCatchBlock(World world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var result = await context.SubCanvasExecutor!.TryExecuteAsync("Body", inputs, cancellationToken);
            if (result.Error is not null) world.Caught.Add(result.Error);
            return new Dictionary<string, object?> { ["caught"] = result.Error is not null };
        }
    }

    private sealed class SlowBlock(World world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref world.SlowExecutions);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class CountedBlock(World world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref world.CountedExecutions);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class SilentMetrics : IWorkflowMetrics
    {
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class SilentObserver : IWorkflowNodeObserver
    {
        public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs,
            long durationMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, string error, long durationMs,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
