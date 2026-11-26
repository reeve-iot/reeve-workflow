using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;
using Xunit.Abstractions;
using MainCheckpoint = ReeveWorkflow.Core.Tests.Execution.PreviousRelease.MainCheckpoint;
using MainPayload = ReeveWorkflow.Core.Tests.Execution.PreviousRelease.WorkflowSuspensionStatePayload;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunCheckpointOutputsTests(ITestOutputHelper output)
{
    private static readonly Guid Journal = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid State = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int Roomy = 1 << 22;
    private const string Frames = """[{"NodeInFlight":"x","PreviousOutputsJson":"{\"a\":\"é<\"}","Note":"frame \"quoted\""}]""";

    private enum Colour { Red, Blue }

    private static string Chars(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> Node(string id, Dictionary<string, object?> values) =>
        new() { [id] = values };

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> Outputs(params (string Id, Dictionary<string, object?> Values)[] nodes)
    {
        var all = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var (id, values) in nodes)
            all[id] = values;
        return all;
    }

    public static TheoryData<string> Sets() => new(SetFactories.Keys);

    private static readonly Dictionary<string, Func<Dictionary<string, IReadOnlyDictionary<string, object?>>>> SetFactories = new()
    {
        ["empty"] = () => new(),
        ["nested-unsorted-keys"] = () => Outputs(
            ("start", new() { ["zeta"] = 1L, ["alpha"] = new Dictionary<string, object?> { ["y"] = 2L, ["b"] = new Dictionary<string, object?> { ["q"] = new object?[] { 3L, 2L, 1L }, ["c"] = null } } }),
            ("http", new() { ["status"] = 200L, ["body"] = new Dictionary<string, object?> { ["z"] = "last", ["a"] = "first", ["m"] = new List<int> { 9, 8 } } })),
        ["unicode"] = () => Outputs(
            ("u", new() { ["latin"] = "héllo wörld".Normalize(), ["cjk"] = "日本語のテキスト", ["pair"] = "😀 🚀", ["separators"] = Chars(0x2028, 0x2029, 0x85), ["controls"] = "tab\t newline\n cr\r ctrl" + Chars(1) + " del" + Chars(0x7f) })),
        ["html-and-quotes"] = () => Outputs(
            ("h", new() { ["markup"] = "<script>alert('x')&amp;+</script>", ["json"] = "{\"a\":\"b\\c\"}", ["slashes"] = "a/b\\c\\\\d" })),
        ["numbers-and-types"] = () => Outputs(
            ("n", new()
            {
                ["max"] = long.MaxValue, ["min"] = long.MinValue, ["half"] = 1.5, ["sum"] = 0.1 + 0.2, ["huge"] = 1e300,
                ["tiny"] = double.Epsilon, ["dec"] = 79228162514264337593543950335m, ["negzero"] = -0.0, ["u"] = ulong.MaxValue,
                ["when"] = new DateTimeOffset(2026, 10, 3, 12, 30, 45, 123, TimeSpan.FromHours(2)), ["id"] = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                ["colour"] = Colour.Blue, ["span"] = TimeSpan.FromMinutes(90), ["flag"] = true, ["none"] = null
            })),
        ["json-elements"] = () => Outputs(
            ("j", new() { ["body"] = JsonDocument.Parse("""{"z":1,"a":[1,2,{"y":"é","b":null}],"m":"<x>"}""").RootElement.Clone() })),
        ["large-strings"] = () => Outputs(
            ("big1", new() { ["blob"] = new string('x', 100_000) }),
            ("big2", new() { ["blob"] = string.Concat(Enumerable.Repeat("<é\"\\>", 20_000)) })),
        ["escaped-node-ids"] = () => Outputs(
            ("weird\"id\\<é", new() { ["v"] = 1L }), ("日本", new() { ["v"] = 2L }), ("start", new() { ["v"] = 3L })),
        ["many-nodes"] = () => Enumerable.Range(0, 60).ToDictionary(
            i => $"node-{i}", i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["value"] = new string((char)('a' + i % 26), 50 + i), ["n"] = (long)i }),
        ["empty-node-outputs"] = () => Outputs(("a", new()), ("b", new() { ["k"] = new Dictionary<string, object?>() }))
    };

    private static string Oracle(
        Dictionary<string, IReadOnlyDictionary<string, object?>> outputs, int maxBytes, string? continuation = null,
        string? inputNode = null, string? reason = null) =>
        MainCheckpoint.Build(outputs, "next", Journal, State, inputNode, null, null, RunStateWriteMode.EveryBlock,
            reason, maxBytes, continuation);

    private static string Built(
        Dictionary<string, IReadOnlyDictionary<string, object?>> outputs, int maxBytes, string? continuation = null,
        string? inputNode = null, string? reason = null) =>
        WorkflowRunCheckpoint.Build(outputs, TestPrograms.Untyped, "next", Journal, State, inputNode, null, null, RunStateWriteMode.EveryBlock,
            reason, maxBytes, continuation);

    private static string BuiltFromRecorded(
        Dictionary<string, IReadOnlyDictionary<string, object?>> outputs, int maxBytes, string? continuation = null,
        string? inputNode = null)
    {
        var fragments = new CheckpointOutputFragments();
        foreach (var (id, values) in outputs)
            fragments.Record(id, values, null, maxBytes);
        return WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, inputNode, null, null,
            RunStateWriteMode.EveryBlock, null, maxBytes, continuation, fragments);
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void A_checkpoint_is_the_previous_releases_bytes_for_every_shape_of_output(string set)
    {
        var outputs = SetFactories[set]();
        foreach (var continuation in new string?[] { null, Frames })
        foreach (var inputNode in new string?[] { null, "start" })
        {
            var expected = Oracle(outputs, Roomy, continuation, inputNode);

            Built(outputs, Roomy, continuation, inputNode).Should().Be(expected, "the wire format is the previous release's");
            BuiltFromRecorded(outputs, Roomy, continuation, inputNode).Should().Be(expected,
                "outputs serialised as their blocks complete are the same bytes");
        }
    }

    [Fact]
    public void A_checkpoint_with_a_reason_or_without_outputs_is_the_previous_releases_bytes()
    {
        var outputs = SetFactories["nested-unsorted-keys"]();
        foreach (var reason in new[] { WorkflowRunCheckpoint.SkippedANode, WorkflowRunCheckpoint.TooLarge })
            Built(outputs, Roomy, null, null, reason).Should().Be(Oracle(outputs, Roomy, null, null, reason));
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void The_cap_is_met_at_exactly_the_size_the_previous_release_measured_and_not_one_byte_over(string set)
    {
        var outputs = SetFactories[set]();
        foreach (var continuation in new string?[] { null, Frames })
        {
            var full = Encoding.UTF8.GetByteCount(Oracle(outputs, Roomy, continuation));
            var thresholds = new List<int> { full, full - 1 };
            if (continuation is not null)
            {
                var root = Encoding.UTF8.GetByteCount(Oracle(outputs, Roomy));
                thresholds.AddRange([root, root - 1]);
            }

            foreach (var cap in thresholds)
            {
                var expected = Oracle(outputs, cap, continuation);
                Built(outputs, cap, continuation).Should().Be(expected, $"cap {cap}");
                BuiltFromRecorded(outputs, cap, continuation).Should().Be(expected, $"recorded, cap {cap}");
            }
        }
    }

    [Fact]
    public void One_byte_past_the_cap_is_too_large_and_at_the_cap_it_is_a_checkpoint()
    {
        var outputs = SetFactories["unicode"]();
        var size = Encoding.UTF8.GetByteCount(Oracle(outputs, Roomy));

        WorkflowRunCheckpoint.Read(Built(outputs, size))!.NotReenterableBecause.Should().BeNull();
        WorkflowRunCheckpoint.Read(Built(outputs, size - 1))!.NotReenterableBecause.Should().Be(WorkflowRunCheckpoint.TooLarge);
    }

    [Fact]
    public void A_cap_that_only_the_frames_overrun_drops_the_frames_and_keeps_the_outputs()
    {
        var outputs = SetFactories["nested-unsorted-keys"]();
        var root = Encoding.UTF8.GetByteCount(Oracle(outputs, Roomy));

        var checkpoint = WorkflowRunCheckpoint.Read(Built(outputs, root, Frames))!;

        checkpoint.NotReenterableBecause.Should().BeNull();
        checkpoint.ContinuationJson.Should().BeNull();
        checkpoint.ContainerInFlightRepeatsWhole.Should().BeTrue();
        PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson).Keys
            .Should().BeEquivalentTo(["start", "http"]);
    }

    [Theory]
    [InlineData(55)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(70)]
    public void An_output_nested_past_the_depth_limit_is_recorded_unserialisable_exactly_where_the_previous_release_did(int depth)
    {
        object value = 1L;
        for (var level = 0; level < depth; level++)
            value = new Dictionary<string, object?> { ["n"] = value };
        var outputs = Outputs(("deep", new() { ["v"] = value }));

        var built = Built(outputs, Roomy);

        built.Should().Be(Oracle(outputs, Roomy));
        BuiltFromRecorded(outputs, Roomy).Should().Be(Oracle(outputs, Roomy), "recording as the block completes never throws either");
    }

    [Fact]
    public void Some_depth_is_too_deep_and_some_is_not()
    {
        string ReasonAt(int depth)
        {
            object value = 1L;
            for (var level = 0; level < depth; level++)
                value = new Dictionary<string, object?> { ["n"] = value };
            return WorkflowRunCheckpoint.Read(Built(Outputs(("deep", new() { ["v"] = value })), Roomy))!.NotReenterableBecause ?? "none";
        }

        ReasonAt(55).Should().Be("none");
        ReasonAt(70).Should().Be(WorkflowRunCheckpoint.Unserialisable);
    }

    [Fact]
    public void A_node_run_again_replaces_its_fragment_and_the_old_bytes_are_never_written()
    {
        var outputs = Outputs(("loop", new() { ["i"] = 1L }), ("other", new() { ["v"] = "kept" }));
        var fragments = new CheckpointOutputFragments();
        foreach (var (id, values) in outputs)
            fragments.Record(id, values, null, Roomy);
        string Cached() => WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
            RunStateWriteMode.EveryBlock, null, Roomy, null, fragments);
        Cached().Should().Be(Oracle(outputs, Roomy));

        outputs["loop"] = new Dictionary<string, object?> { ["i"] = 2L, ["extra"] = "é" };

        Cached().Should().Be(Oracle(outputs, Roomy), "the new instance is read even when nobody recorded it");
        Cached().Should().Contain("extra");
        outputs["loop"] = new Dictionary<string, object?> { ["i"] = 3L };
        fragments.Record("loop", outputs["loop"], null, Roomy);
        Cached().Should().Be(Oracle(outputs, Roomy)).And.NotContain("extra");
    }

    [Fact]
    public void A_node_that_is_gone_from_the_outputs_is_not_written()
    {
        var outputs = Outputs(("a", new() { ["v"] = 1L }), ("b", new() { ["v"] = 2L }));
        var fragments = new CheckpointOutputFragments();
        foreach (var (id, values) in outputs)
            fragments.Record(id, values, null, Roomy);
        outputs.Remove("b");

        WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
            RunStateWriteMode.EveryBlock, null, Roomy, null, fragments).Should().Be(Oracle(outputs, Roomy));
    }

    [Fact]
    public void Concurrent_builds_over_one_cache_all_write_the_previous_releases_bytes()
    {
        var outputs = SetFactories["many-nodes"]();
        var expected = Oracle(outputs, Roomy);
        var results = new ConcurrentBag<string>();

        for (var round = 0; round < 40; round++)
        {
            var fragments = new CheckpointOutputFragments();
            using var start = new Barrier(8);
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                start.SignalAndWait();
                results.Add(WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
                    RunStateWriteMode.EveryBlock, null, Roomy, null, fragments));
            });
        }

        results.Should().HaveCount(320).And.OnlyContain(json => json == expected);
    }

    private sealed class Counts
    {
        private readonly ConcurrentDictionary<int, int> calls = new();
        public int Of(int id) => calls.GetValueOrDefault(id);
        public int Total => calls.Values.Sum();
        public void Hit(int id) => calls.AddOrUpdate(id, 1, (_, n) => n + 1);
    }

    [JsonConverter(typeof(CountedValueConverter))]
    private sealed record CountedValue(int Id, Counts Counts);

    private static readonly CompilationOptions CountedOptions = new()
    {
        TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Counted", typeof(CountedValue))
        {
            JsonTypeInfo = StrictJson.Info(typeof(CountedValue)),
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
        }]),
    };

    private sealed class CountedValueConverter : JsonConverter<CountedValue>
    {
        public override CountedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetInt32(), new Counts());

        public override void Write(Utf8JsonWriter writer, CountedValue value, JsonSerializerOptions options)
        {
            value.Counts.Hit(value.Id);
            writer.WriteNumberValue(value.Id);
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> CountedNode(Counts counts, int id) =>
        Outputs(($"node-{id}", new() { ["value"] = new CountedValue(id, counts), ["pad"] = new string('p', 64) }));

    [Fact]
    public void Each_nodes_converter_runs_once_across_all_the_checkpoints_of_a_run()
    {
        var counts = new Counts();
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        var fragments = new CheckpointOutputFragments();

        for (var id = 0; id < 32; id++)
        {
            var node = CountedNode(counts, id).Single();
            outputs[node.Key] = node.Value;
            fragments.Record(node.Key, node.Value, null, Roomy);
            WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, $"node-{id + 1}", Journal, State, null, null, null,
                RunStateWriteMode.EveryBlock, null, Roomy, Frames, fragments);
            WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, $"node-{id + 1}", Journal, State, null, null, null,
                RunStateWriteMode.EveryBlock, null, Roomy, null, fragments);
        }

        counts.Total.Should().Be(32);
        Enumerable.Range(0, 32).Should().OnlyContain(id => counts.Of(id) == 1);
    }

    [Fact]
    public void An_output_that_can_never_fit_is_rejected_without_serialising_the_nodes_after_it()
    {
        var counts = new Counts();
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["small"] = new Dictionary<string, object?> { ["value"] = new CountedValue(0, counts) },
            ["big"] = new Dictionary<string, object?> { ["blob"] = new string('x', 50_000) },
            ["after1"] = new Dictionary<string, object?> { ["value"] = new CountedValue(1, counts) },
            ["after2"] = new Dictionary<string, object?> { ["value"] = new CountedValue(2, counts) }
        };

        var json = WorkflowRunCheckpoint.Build(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
            RunStateWriteMode.EveryBlock, null, maxBytes: 4096);

        WorkflowRunCheckpoint.Read(json)!.NotReenterableBecause.Should().Be(WorkflowRunCheckpoint.TooLarge);
        counts.Of(1).Should().Be(0);
        counts.Of(2).Should().Be(0);
        json.Should().Be(Oracle(Plain(outputs), 4096), "what is recorded is what the previous release recorded");
    }

    [Fact]
    public void Outputs_that_did_not_fit_a_smaller_cap_are_written_in_full_under_a_larger_one()
    {
        var outputs = SetFactories["large-strings"]();
        var fragments = new CheckpointOutputFragments();
        foreach (var (id, values) in outputs)
            fragments.Record(id, values, null, maxBytes: 1024);

        WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
            RunStateWriteMode.EveryBlock, null, 1024, null, fragments)
            .Should().Be(Oracle(outputs, 1024));
        WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, "next", Journal, State, null, null, null,
            RunStateWriteMode.EveryBlock, null, Roomy, null, fragments)
            .Should().Be(Oracle(outputs, Roomy), "fragments dropped for being over the old cap are serialised again");
    }

    [Fact]
    public void A_fragment_over_the_cap_keeps_its_length_and_drops_its_bytes()
    {
        var fragments = new CheckpointOutputFragments();
        var small = new Dictionary<string, object?> { ["v"] = 1L };
        var big = new Dictionary<string, object?> { ["blob"] = new string('x', 5000) };
        fragments.Record("small", small, null, 1024);
        fragments.Record("big", big, null, 1024);

        fragments.Get("small", small, null, 1024, needBytes: false).Bytes.Should().NotBeNull();
        var kept = fragments.Get("big", big, null, 1024, needBytes: false);
        kept.Bytes.Should().BeNull("a megabyte HTTP body that can never fit is not held until the run ends");
        kept.Length.Should().BeGreaterThan(5000);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> Plain(
        Dictionary<string, IReadOnlyDictionary<string, object?>> outputs) =>
        outputs.ToDictionary(
            node => node.Key,
            node => (IReadOnlyDictionary<string, object?>)node.Value.ToDictionary(
                value => value.Key, value => value.Value is CountedValue counted ? counted.Id : value.Value));

    private long Allocated(int blocks, bool previousRelease, out long checkpointBytes)
    {
        var nodes = Enumerable.Range(0, blocks)
            .Select(index => new Dictionary<string, object?> { ["value"] = new string((char)('a' + index % 26), 1000) })
            .ToArray();
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        var fragments = new CheckpointOutputFragments();
        long written = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < blocks; index++)
        {
            var id = $"node-{index}";
            outputs[id] = nodes[index];
            string json;
            if (previousRelease)
            {
                json = MainCheckpoint.Build(outputs, id, Journal, State, null, null, null, RunStateWriteMode.EveryBlock, null, Roomy);
            }
            else
            {
                fragments.Record(id, nodes[index], null, Roomy);
                json = WorkflowRunCheckpoint.BuildCached(outputs, TestPrograms.Untyped, id, Journal, State, null, null, null,
                    RunStateWriteMode.EveryBlock, null, Roomy, null, fragments);
            }

            written += json.Length * 2L;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        checkpointBytes = written;
        return allocated;
    }

    [Fact]
    public void Allocation_beyond_the_checkpoint_strings_grows_with_the_blocks_and_stays_below_the_previous_release()
    {
        Allocated(8, false, out _); Allocated(32, false, out _); Allocated(8, true, out _); Allocated(32, true, out _);

        var a8 = Allocated(8, false, out var strings8);
        var a32 = Allocated(32, false, out var strings32);
        var old8 = Allocated(8, true, out _);
        var old32 = Allocated(32, true, out _);
        var extra8 = a8 - strings8;
        var extra32 = a32 - strings32;
        output.WriteLine($"8 blocks: {a8} B ({strings8} B of checkpoint strings, {extra8} B beyond them), previous release {old8} B");
        output.WriteLine($"32 blocks: {a32} B ({strings32} B of checkpoint strings, {extra32} B beyond them), previous release {old32} B");

        extra32.Should().BeLessThan(extra8 * 5, "beyond the strings the store is handed, the cost per block is constant: 4x the blocks, about 4x");
        (extra32 / 32).Should().BeLessThan(16 * 1024, "a block pays for its own output and the envelope, not for the run's history");
        a8.Should().BeLessThan(old8);
        a32.Should().BeLessThan(old32);
        a32.Should().BeLessThan(old32 * 6 / 10, "at 32 blocks of 1 KB the previous release allocates far more per checkpoint");
    }

    [Fact]
    public async Task A_run_serialises_each_root_blocks_outputs_once_across_every_checkpoint_including_those_inside_a_container()
    {
        var counts = new Counts();
        const int blocks = 6;
        var nodes = Enumerable.Range(0, blocks).Select(index => new
        {
            id = $"c{index}", blockTypeId = "test.counted", position = new { x = 0, y = 0 }, configuration = new { index }
        }).ToArray<object>();
        var edges = new List<object> { new { id = "e-start", sourceNodeId = "start", targetNodeId = "c0", mappings = new { } } };
        edges.AddRange(Enumerable.Range(1, blocks - 1).Select(index => (object)new { id = $"e{index}", sourceNodeId = $"c{index - 1}", targetNodeId = $"c{index}", mappings = new { } }));
        edges.Add(new { id = "e-loop", sourceNodeId = $"c{blocks - 1}", targetNodeId = "loop", mappings = new { } });
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new object[] { new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } } }
                .Concat(nodes).Append(new
                {
                    id = "loop", blockTypeId = RunLifecycleWorld.LoopType,
                    subCanvases = new Dictionary<string, object>
                    {
                        ["Body"] = new
                        {
                            nodes = new object[]
                            {
                                new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                new { id = RunLifecycleWorld.HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                            },
                            edges = new object[] { new { id = "e-in-hold", sourceNodeId = "in", targetNodeId = RunLifecycleWorld.HoldNodeId, mappings = new { } } }
                        }
                    }, position = new { x = 0, y = 0 }, configuration = new { }
                }).ToArray(),
            edges
        });
        var world = new RunLifecycleWorld(definitionJson: definition) { LoopIterations = 1 };
        world.AdditionalBlocks = id => id == "test.counted" ? new CountedFactory(counts) : null;
        world.CompilationOptions = CountedOptions;
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await Task.WhenAny(world.Entered.Task, run).WaitAsync(TimeSpan.FromSeconds(10));
        if (run.IsCompleted)
            await run;

        var checkpoint = WorkflowRunCheckpoint.Read(world.RunState.Row(world.RunState.LastBegun!.Value).VariablesJson)!;
        WorkflowRunCheckpoint.Continuation(checkpoint).Should().NotBeEmpty("the write inside the container went through the nested path");
        PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson).Keys.Should().HaveCount(blocks + 1);
        counts.Total.Should().Be(blocks);
        Enumerable.Range(0, blocks).Should().OnlyContain(index => counts.Of(index) == 1,
            "each root block's output is serialised once for all the checkpoints after it, at the root and below it");

        world.Release.TrySetResult();
        await run;
        counts.Total.Should().Be(blocks);
    }

    [Fact]
    public async Task A_root_blocks_outputs_are_serialised_when_it_completes_not_when_a_checkpoint_first_needs_them()
    {
        var counts = new Counts();
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "c0", blockTypeId = "test.counted", position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "c1", blockTypeId = "test.counted", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "e0", sourceNodeId = "start", targetNodeId = "c0", mappings = new { } },
                new { id = "e1", sourceNodeId = "c0", targetNodeId = "c1", mappings = new { } }
            }
        });
        var world = new RunLifecycleWorld(definitionJson: definition);
        world.AdditionalBlocks = id => id == "test.counted" ? new CountedFactory(counts) : null;
        world.CompilationOptions = CountedOptions;

        await world.Engine().ExecuteAsync(world.WorkflowId, null, null);

        counts.Of(0).Should().Be(1);
        counts.Of(1).Should().Be(1, "no checkpoint follows the last block, and its outputs were still written once as it completed");
    }

    private sealed class CountedFactory(Counts counts) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "test.counted";
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => string.Empty;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("value", "value", PortValueType.Of("Counted"), false)];
        public bool IsIdempotent => false;
        public IWorkflowBlockPlugin Create() => new Plugin(counts);

        private sealed class Plugin(Counts counts) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
                {
                    ["value"] = new CountedValue(int.Parse(context.NodeId!.AsSpan(1)), counts)
                });
        }
    }

    [Fact]
    public void The_previous_releases_reader_reads_a_checkpoint_written_here_and_resumes_the_same_outputs()
    {
        var outputs = SetFactories["nested-unsorted-keys"]();

        var json = Built(outputs, Roomy, Frames, "start");
        var theirs = JsonSerializer.Deserialize<MainPayload>(json, WorkflowGraphHelpers.JsonOptions)!;
        var oracle = JsonSerializer.Deserialize<MainPayload>(Oracle(outputs, Roomy, Frames, "start"), WorkflowGraphHelpers.JsonOptions)!;

        theirs.PreviousOutputsJson.Should().Be(oracle.PreviousOutputsJson);
        theirs.NodeInFlight.Should().Be("next");
        theirs.ContinuationJson.Should().Be(Frames);
        JsonSerializer.Serialize(PersistedValueCodec.ForDisplay(theirs.PreviousOutputsJson), WorkflowGraphHelpers.JsonOptions)
            .Should().Be(JsonSerializer.Serialize(PersistedValueCodec.ForDisplay(oracle.PreviousOutputsJson), WorkflowGraphHelpers.JsonOptions));
        JsonSerializer.Serialize(theirs, WorkflowGraphHelpers.JsonOptions).Should().Be(json, "no member is added or dropped on the way through");
    }

    [Fact]
    public void The_previous_releases_reader_reads_a_parked_payload_written_here_and_resumes_the_same_outputs()
    {
        var outputs = SetFactories["unicode"]();

        var json = WorkflowExecutionHelpers.BuildSuspensionStatePayload(outputs, TestPrograms.Untyped, null, null, inputNodeId: null, Frames, "next");
        var theirs = JsonSerializer.Deserialize<MainPayload>(json, WorkflowGraphHelpers.JsonOptions)!;

        theirs.PreviousOutputsJson.Should().Be(JsonSerializer.Serialize(
            outputs.ToDictionary(node => node.Key, node => (object?)node.Value), WorkflowGraphHelpers.JsonOptions));
        PersistedValueCodec.ForDisplay(theirs.PreviousOutputsJson).Keys.Should().BeEquivalentTo(outputs.Keys);
        JsonSerializer.Serialize(theirs, WorkflowGraphHelpers.JsonOptions).Should().Be(json);
    }

    [Fact]
    public void A_checkpoint_the_previous_release_wrote_is_read_here_and_resumes_the_same_outputs()
    {
        var outputs = SetFactories["unicode"]();
        var theirs = Oracle(outputs, Roomy, Frames, "start");

        var read = WorkflowRunCheckpoint.Read(theirs)!;

        read.NodeInFlight.Should().Be("next");
        read.ContinuationJson.Should().Be(Frames);
        JsonSerializer.Serialize(read, WorkflowGraphHelpers.JsonOptions).Should().Be(theirs);
        PersistedValueCodec.ForDisplay(read.PreviousOutputsJson).Keys.Should().BeEquivalentTo(outputs.Keys);
        WorkflowRunCheckpoint.Variables(theirs).Should().ContainKey("latin");
    }

    [Fact]
    public void A_parked_payload_the_previous_release_wrote_is_read_here()
    {
        var outputs = SetFactories["unicode"]();
        var theirs = JsonSerializer.Serialize(new MainPayload
        {
            VariablesJson = "{}",
            PreviousOutputsJson = JsonSerializer.Serialize(outputs.ToDictionary(node => node.Key, node => (object?)node.Value), WorkflowGraphHelpers.JsonOptions),
            TriggerEnvelopeJson = "{}",
            ExecutionStateId = State
        }, WorkflowGraphHelpers.JsonOptions);

        var read = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(theirs, WorkflowGraphHelpers.JsonOptions)!;

        PersistedValueCodec.ForDisplay(read.PreviousOutputsJson).Keys.Should().BeEquivalentTo(outputs.Keys);
        JsonSerializer.Serialize(read, WorkflowGraphHelpers.JsonOptions).Should().Be(theirs);
    }

    [Fact]
    public void The_outputs_property_is_a_json_string_in_a_checkpoint_and_in_a_parked_payload()
    {
        var outputs = SetFactories["nested-unsorted-keys"]();

        foreach (var json in new[]
        {
            Built(outputs, Roomy),
            WorkflowExecutionHelpers.BuildSuspensionStatePayload(outputs, TestPrograms.Untyped, null, null, null)
        })
        {
            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty(nameof(WorkflowSuspensionStatePayload.PreviousOutputsJson)).ValueKind
                .Should().Be(JsonValueKind.String, "a jsonb column keeps a string as it was written and re-orders an object");
        }
    }

    [Fact]
    public void A_pooled_buffer_is_returned_cleared_and_disposing_twice_is_harmless()
    {
        var buffer = new PooledBuffer(64);
        buffer.Write("secret-bytes"u8);
        buffer.Dispose();
        buffer.Dispose();

        var again = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        again.AsSpan().ToArray().Should().OnlyContain(value => value == 0);
        System.Buffers.ArrayPool<byte>.Shared.Return(again);
        var disposed = () => buffer.Write("x"u8);
        disposed.Should().Throw<ObjectDisposedException>();
    }
}
