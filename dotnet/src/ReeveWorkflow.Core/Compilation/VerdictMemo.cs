namespace ReeveWorkflow.Core.Compilation;

internal sealed class VerdictMemo
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, MemoEntry> entries = [];
    private readonly Queue<Guid> installOrder = new();
    private readonly int capacity;
    private int enqueued;

    internal VerdictMemo(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    internal int QueueLength
    {
        get
        {
            lock (gate)
                return installOrder.Count;
        }
    }

    internal int Enqueued
    {
        get
        {
            lock (gate)
                return enqueued;
        }
    }

    internal MemoEntry? Read(Guid versionId)
    {
        lock (gate)
            return entries.GetValueOrDefault(versionId);
    }

    internal bool Replace(Guid versionId, MemoEntry? observed, MemoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (gate)
        {
            var isNewKey = !entries.ContainsKey(versionId);
            if (!ReferenceEquals(entries.GetValueOrDefault(versionId), observed))
                return false;

            entries[versionId] = entry;
            if (isNewKey)
            {
                installOrder.Enqueue(versionId);
                enqueued++;
            }
            if (entries.Count > capacity)
                EvictOldest();

            return true;
        }
    }

    private void EvictOldest()
    {
        for (var target = entries.Count / 2; target > 0; target--)
            entries.Remove(installOrder.Dequeue());
    }
}

internal sealed class MemoEntry(
    string definitionDigest, string dependencyHash, CompilationVerdict verdict, bool saved, WorkflowProgram? program = null)
{
    internal string DefinitionDigest { get; } = definitionDigest;

    internal string DependencyHash { get; } = dependencyHash;

    internal CompilationVerdict Verdict { get; } = verdict;

    internal bool Saved { get; } = saved;

    internal WorkflowProgram? Program { get; } = program;

    internal MemoEntry AsSaved() => new(DefinitionDigest, DependencyHash, Verdict, saved: true, Program);
}
