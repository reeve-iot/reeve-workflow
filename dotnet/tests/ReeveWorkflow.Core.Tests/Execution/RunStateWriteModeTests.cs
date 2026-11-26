using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RunStateWriteModeTests
{
    private const string Idempotent = "test.idempotent";
    private const string NotIdempotent = "test.not-idempotent";

    [Fact]
    public async Task Every_block_writes_before_every_block()
    {
        var written = await WritesOf(RunStateWriteMode.EveryBlock, Chain);

        written.Should().Equal("a", "b", "n", "c", "d");
    }

    [Fact]
    public async Task Before_non_idempotent_only_writes_before_the_first_block_the_one_that_is_not_and_the_one_after_it()
    {
        var written = await WritesOf(RunStateWriteMode.BeforeNonIdempotentOnly, Chain);

        written.Should().Equal(["a", "n", "c"],
            "between two writes only idempotent blocks run, so a recovery repeats nothing else");
    }

    [Fact]
    public async Task Before_non_idempotent_only_counts_a_container_by_everything_inside_it()
    {
        var written = await WritesOf(RunStateWriteMode.BeforeNonIdempotentOnly, BoxWithNonIdempotentInside);

        written.Should().Equal(["a", "box", "n", "d"],
            "a recovery re-enters at the root and repeats the container whole, so the checkpoint must name it before "
            + "the block inside runs, and the block after it moves the checkpoint past it");
    }

    [Fact]
    public async Task Before_non_idempotent_only_writes_the_root_block_after_a_container_whatever_ran_last_inside_it()
    {
        var written = await WritesOf(RunStateWriteMode.BeforeNonIdempotentOnly, BoxEndingIdempotent);

        written.Should().Equal(["a", "box", "n", "m", "y"],
            "the root block after the container moves the checkpoint past it; without that write a recovery asks about "
            + "the container, which is finished, and refuses a run it could re-enter");
    }

    [Fact]
    public async Task Heartbeat_writes_only_once_the_last_write_is_old_enough()
    {
        var written = await WritesOf(RunStateWriteMode.Heartbeat(TimeSpan.FromHours(1)), Chain);

        written.Should().Equal(["a"], "the first block always writes, and an hour does not pass in this run");
    }

    [Fact]
    public async Task Heartbeat_with_a_short_interval_writes_as_often_as_it_elapses()
    {
        var written = await WritesOf(RunStateWriteMode.Heartbeat(TimeSpan.FromTicks(1)), Chain);

        written.Should().Equal("a", "b", "n", "c", "d");
    }

    [Fact]
    public async Task The_mode_that_wrote_a_checkpoint_is_named_in_it()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null, WriteMode = RunStateWriteMode.Heartbeat(TimeSpan.FromHours(1)) };
        var checkpoints = new ConcurrentQueue<string>();
        world.RunState.OnTransition = request => checkpoints.Enqueue(request.VariablesJson);
        world.Blocks.Extra = Blocks;
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.AddRaw(Chain), null, null);

        WorkflowRunCheckpoint.Read(checkpoints.First())!.WriteMode.Should().Be(nameof(RunStateWriteModeKind.Heartbeat),
            "a recovery reads from it how far behind the checkpoint may be");
    }

    private static async Task<string[]> WritesOf(RunStateWriteMode mode, string definition)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null, WriteMode = mode };
        var written = new ConcurrentQueue<string>();
        world.RunState.OnTransition = request =>
        {
            if (request is { FromStatus: WorkflowRunStateStatus.Running, ToStatus: WorkflowRunStateStatus.Running })
                written.Enqueue(request.CurrentBlockPath!);
        };
        world.Blocks.Extra = Blocks;
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.AddRaw(definition), null, null);

        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        return [.. written];
    }

    private static IWorkflowBlockFactory? Blocks(string blockTypeId) => blockTypeId switch
    {
        Idempotent => new Factory(Idempotent, true),
        NotIdempotent => new Factory(NotIdempotent, false),
        _ => null
    };

    private static readonly string Chain = JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "a", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "b", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "n", blockTypeId = NotIdempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "c", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "d", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = Edges("start", "a", "b", "n", "c", "d")
    });

    private static readonly string BoxWithNonIdempotentInside = JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "a", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "b", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.ContainerType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "n", blockTypeId = NotIdempotent, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = Edges("in", "n")
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = "d", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "e", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = Edges("start", "a", "b", "box", "d", "e")
    });

    private static readonly string BoxEndingIdempotent = JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "a", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.ContainerType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "n", blockTypeId = NotIdempotent, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "m", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = Edges("in", "n", "m")
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = "y", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "z", blockTypeId = Idempotent, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = Edges("start", "a", "box", "y", "z")
    });

    private static object[] Edges(params string[] chain) =>
        [.. chain.Zip(chain.Skip(1), (from, to) => new { id = $"{from}-{to}", sourceNodeId = from, targetNodeId = to, mappings = new { } })];

    private sealed class Factory(string id, bool idempotent) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => idempotent;
        public IWorkflowBlockPlugin Create() => new Plugin();
    }

    private sealed class Plugin : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }
}
