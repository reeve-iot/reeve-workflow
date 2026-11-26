using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowVersionDataCacheTests
{
    [Fact]
    public void Reuses_data_for_a_version_and_rebuilds_after_content_changes()
    {
        var id = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var first = Version(id, workflowId, "{\"nodes\":[],\"edges\":[]}");
        var parsed = WorkflowVersionDataCache.Get(first);
        WorkflowVersionDataCache.Get(first).Should().BeSameAs(parsed);
        var digest = parsed.DigestFor(null, null);
        var definition = parsed.Definition;
        var warm = WorkflowVersionDataCache.Get(first);
        warm.DigestFor(null, null).Should().BeSameAs(digest);
        warm.Definition.Should().BeSameAs(definition);

        first.UpdateDefinition("{\"nodes\":[{\"id\":\"changed\"}],\"edges\":[]}", null, null);
        var changed = WorkflowVersionDataCache.Get(first);
        changed.Should().NotBeSameAs(parsed);
        changed.DigestFor(null, null).Should().NotBe(parsed.DigestFor(null, null));

        var nextVersion = WorkflowVersionDataCache.Get(Version(Guid.NewGuid(), workflowId, "{\"nodes\":[],\"edges\":[]}"));
        nextVersion.Should().NotBeSameAs(parsed);
    }

    [Fact]
    public void The_entry_does_not_depend_on_the_parameters_only_its_digest_does()
    {
        var version = Version(Guid.NewGuid(), Guid.NewGuid(), "{\"nodes\":[],\"edges\":[]}");
        var entry = WorkflowVersionDataCache.Get(version);
        var bare = entry.DigestFor(null, null);

        entry.DigestFor("[]", null).Should().NotBe(bare);
        WorkflowVersionDataCache.Get(version).Should().BeSameAs(entry);
        entry.DigestFor("[]", null).Should().BeSameAs(entry.DigestFor("[]", null));
    }

    [Fact]
    public void Privilege_is_checked_against_the_live_registry_for_a_cached_definition()
    {
        var entry = WorkflowVersionDataCache.Get(Guid.NewGuid(), "{\"nodes\":[{\"blockTypeId\":\"plugin.x\"}],\"edges\":[]}");
        var mutableBlocks = new MutableBlocks();
        IWorkflowBlocks blocks = mutableBlocks;

        blocks.ContainsPrivilegedBlock(entry.BlockTypes).Should().BeFalse();
        mutableBlocks.Privileged = true;
        blocks.ContainsPrivilegedBlock(entry.BlockTypes).Should().BeTrue();
    }

    [Fact]
    public void Warm_cache_allocation_does_not_scale_with_definition_text_size()
    {
        var small = MeasureWarmLookups(5_000);
        var large = MeasureWarmLookups(50_000);

        Math.Abs(large - small).Should().BeLessThan(20_000);
    }

    [Fact]
    public async Task Concurrent_reads_of_one_version_share_one_entry()
    {
        var version = Version(Guid.NewGuid(), Guid.NewGuid(), "{\"nodes\":[],\"edges\":[]}");
        var entries = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(index => Task.Run(() =>
            {
                var entry = WorkflowVersionDataCache.Get(version);
                _ = entry.Definition;
                _ = entry.DigestFor(null, null);
                return entry;
            })));
        entries.Should().OnlyContain(entry => ReferenceEquals(entry, entries[0]));
        entries.Select(entry => entry.Definition).Distinct(ReferenceEqualityComparer.Instance).Should().ContainSingle();
        entries.Select(entry => entry.DigestFor(null, null)).Distinct(ReferenceEqualityComparer.Instance).Should().ContainSingle();
    }

    private static WorkflowVersion Version(Guid id, Guid workflowId, string json) =>
        new(id, workflowId, 1, json, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static long MeasureWarmLookups(int payloadLength)
    {
        var json = "{\"nodes\":[{\"id\":\"n\",\"blockTypeId\":\"x\",\"configuration\":{\"payload\":\""
            + new string('x', payloadLength) + "\"}}],\"edges\":[]}";
        var id = Guid.NewGuid();
        var entry = WorkflowVersionDataCache.Get(id, json);
        _ = entry.ReadStored(out _);
        _ = entry.ShapesUsed("test", EmptyShapeSource.Instance);
        for (var i = 0; i < 10; i++)
        {
            var warm = WorkflowVersionDataCache.Get(id, json);
            _ = warm.ReadStored(out _);
            _ = warm.ShapesUsed("test", EmptyShapeSource.Instance);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var warm = WorkflowVersionDataCache.Get(id, json);
            _ = warm.ReadStored(out _);
            _ = warm.ShapesUsed("test", EmptyShapeSource.Instance);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class EmptyShapeSource : IBlockShapeSource
    {
        internal static EmptyShapeSource Instance { get; } = new();
        public BlockShape? Find(string blockTypeId) => null;
    }

    private sealed class MutableBlocks : IWorkflowBlocks
    {
        public bool Privileged { get; set; }
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == "plugin.x" ? new PrivilegedFactory(Privileged) : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class PrivilegedFactory(bool privileged) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "plugin.x";
        public string DisplayName => "X";
        public string Description => "X";
        public string Category => "Test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsPrivileged => privileged;
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
