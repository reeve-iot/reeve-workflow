using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowVersionDataCache
{
    private const int Capacity = 256;
    private const long ByteCapacity = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<Guid, Lazy<Entry>> Entries = new();
    private static readonly object EvictionGate = new();
    private static readonly LinkedList<(Guid Id, Lazy<Entry> Value)> InsertionOrder = new();
    private static readonly Dictionary<Guid, LinkedListNode<(Guid Id, Lazy<Entry> Value)>> QueueNodes = [];
    private static long retainedBytes;

    internal static Entry Get(WorkflowVersion version) => Get(version.Id, version.DefinitionJson);

    internal static Entry Get(Guid id, string definition)
    {
        while (true)
        {
            var hasExisting = Entries.TryGetValue(id, out var existing);
            if (hasExisting && existing!.Value.Matches(definition))
                return existing.Value;
            var proposed = new Lazy<Entry>(() => Entry.Create(definition), LazyThreadSafetyMode.ExecutionAndPublication);
            var installed = hasExisting
                ? Entries.TryUpdate(id, proposed, existing!)
                : ReferenceEquals(Entries.GetOrAdd(id, proposed), proposed);
            if (!installed)
                continue;

            var candidate = proposed.Value;
            lock (EvictionGate)
            {
                if (Entries.TryGetValue(id, out var current) && ReferenceEquals(current, proposed))
                {
                    if (QueueNodes.Remove(id, out var previousNode))
                    {
                        InsertionOrder.Remove(previousNode);
                        retainedBytes -= previousNode.Value.Value.Value.RetainedBytes;
                    }
                    retainedBytes += candidate.RetainedBytes;
                    QueueNodes[id] = InsertionOrder.AddLast((id, proposed));
                    while ((Entries.Count > Capacity || retainedBytes > ByteCapacity) && InsertionOrder.First is { } first)
                    {
                        InsertionOrder.RemoveFirst();
                        QueueNodes.Remove(first.Value.Id);
                        if (Entries.TryGetValue(first.Value.Id, out var currentEntry) && ReferenceEquals(currentEntry, first.Value.Value))
                            Entries.TryRemove(new KeyValuePair<Guid, Lazy<Entry>>(first.Value.Id, first.Value.Value));
                        retainedBytes -= first.Value.Value.Value.RetainedBytes;
                    }
                }
            }
            return candidate;
        }
    }

    internal sealed class Entry
    {
        private readonly string definitionJson;
        private readonly object digestGate = new();
        private DigestSlot? digestSlot;
        private readonly Lazy<ParsedDefinition> parsed;
        private readonly Lazy<IReadOnlySet<string>> blockTypes;
        private readonly Lazy<StoredDocumentResult> storedDocument;
        private readonly ConditionalWeakTable<IBlockShapeSource, ConcurrentDictionary<string, IReadOnlyCollection<BlockShape>>>
            shapesBySource = new();

        private readonly ConditionalWeakTable<ExpressionKeyShapes, IReadOnlyCollection<HelperPath>> helperPathsByShapes = new();

        private Entry(string definition)
        {
            definitionJson = definition;
            parsed = new Lazy<ParsedDefinition>(() => Parse(definition), LazyThreadSafetyMode.ExecutionAndPublication);
            blockTypes = new Lazy<IReadOnlySet<string>>(() => WorkflowDefinitionBlockTypes.Collect(definition), LazyThreadSafetyMode.ExecutionAndPublication);
            storedDocument = new Lazy<StoredDocumentResult>(() =>
            {
                var document = StoredWorkflowDocument.Read(definition, out var fault);
                return new StoredDocumentResult(document, fault);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal WorkflowDefinition? Definition => parsed.Value.Definition;

        internal string DigestFor(string? inputs, string? outputs)
        {
            var slot = Volatile.Read(ref digestSlot);
            if (slot is not null && slot.Inputs == inputs && slot.Outputs == outputs)
                return slot.Digest;

            lock (digestGate)
            {
                slot = digestSlot;
                if (slot is null || slot.Inputs != inputs || slot.Outputs != outputs)
                {
                    slot = new DigestSlot(inputs, outputs,
                        WorkflowCompilationService.ComputeDefinitionDigest(definitionJson, inputs, outputs));
                    Volatile.Write(ref digestSlot, slot);
                }

                return slot.Digest;
            }
        }

        internal DefinitionParseFailure? ParseFailure => parsed.Value.Failure;
        internal IReadOnlySet<string> BlockTypes => blockTypes.Value;
        internal StoredWorkflowDocument? ReadStored(out StoredDocumentFault? fault)
        {
            var result = storedDocument.Value;
            fault = result.Fault;
            return result.Document;
        }
        internal IReadOnlyCollection<BlockShape> ShapesUsed(string generation, IBlockShapeSource source) =>
            shapesBySource.GetValue(source, sourceKey => new ConcurrentDictionary<string, IReadOnlyCollection<BlockShape>>(StringComparer.Ordinal))
            .GetOrAdd(generation, generationKey =>
            {
                var document = ReadStored(out _);
                return document is null ? Array.Empty<BlockShape>() : WorkflowCompiler.ShapesUsedBy(document.Definition, source).Values.ToArray();
            });
        internal IReadOnlyCollection<HelperPath> HelperPathsRead(ExpressionKeyShapes keyShapes) =>
            helperPathsByShapes.GetValue(keyShapes, shapes =>
                ReadStored(out _) is { } document ? [.. HelperPathsInDocument.Collect(document.Definition, shapes)] : []);
        internal long RetainedBytes => (long)definitionJson.Length * sizeof(char);
        internal bool Matches(string definition) => definitionJson == definition;

        internal static Entry Create(string definition) => new(definition);

        private static ParsedDefinition Parse(string definition)
        {
            try
            {
                return new ParsedDefinition(WorkflowDefinitionParse.FromText(definition), null);
            }
            catch (JsonException ex)
            {
                return new ParsedDefinition(null, new DefinitionParseFailure(ex.Message, ex.Path, ex.LineNumber, ex.BytePositionInLine));
            }
        }
    }

    internal sealed record DefinitionParseFailure(string Message, string? Path, long? LineNumber, long? BytePositionInLine)
    {
        internal JsonException CreateException() => new(Message, Path, LineNumber, BytePositionInLine);
    }

    private sealed record DigestSlot(string? Inputs, string? Outputs, string Digest);
    private sealed record ParsedDefinition(WorkflowDefinition? Definition, DefinitionParseFailure? Failure);
    private sealed record StoredDocumentResult(StoredWorkflowDocument? Document, StoredDocumentFault? Fault);
}
