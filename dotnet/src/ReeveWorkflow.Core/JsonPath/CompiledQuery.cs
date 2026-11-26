using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ReeveWorkflow.Core.JsonPath;

internal readonly record struct JsonPathNode(JsonElement Value, PathLink? Path);

internal interface INodeSink
{
    bool Add(in JsonPathNode node);
}

internal interface IBudgetedSink
{
    bool Add(in JsonPathNode node, ref JsonPathBudget budget);
}

internal readonly struct EvaluationContext(bool trackPaths)
{
    public bool TrackPaths { get; } = trackPaths;
}

internal abstract class Selector;

internal sealed class NameSelector(string name) : Selector
{
    public string Name { get; } = name;
    public byte[] Utf8Name { get; } = Encoding.UTF8.GetBytes(name);
}

internal sealed class WildcardSelector : Selector
{
    public static readonly WildcardSelector Instance = new();
}

internal sealed class IndexSelector(long index) : Selector
{
    public long Index { get; } = index;
}

internal sealed class SliceSelector(long? start, long? end, long? step) : Selector
{
    public long? Start { get; } = start;
    public long? End { get; } = end;
    public long Step { get; } = step ?? 1;
}

internal sealed class Segment(Selector[] selectors)
{
    public Selector[] Selectors { get; } = selectors;
}

internal sealed class CompiledQuery
{
    public CompiledQuery(Segment[] segments)
    {
        Segments = segments;
        IsSingular = segments.All(segment =>
            segment.Selectors.Length == 1
            && segment.Selectors[0] is NameSelector or IndexSelector);
    }

    public bool IsSingular { get; }
    public Segment[] Segments { get; }

    public void Run<TSink>(JsonElement start, in EvaluationContext context, ref TSink sink)
        where TSink : struct, INodeSink
    {
        var budget = new JsonPathBudget();
        if (Segments.Length == 0)
        {
            budget.Select(start);
            sink.Add(new JsonPathNode(start, null));
            return;
        }

        var current = BufferPool<JsonPathNode>.Rent();
        try
        {
            current.Add(new JsonPathNode(start, null));
            for (var i = 0; i < Segments.Length - 1; i++)
            {
                var next = BufferPool<JsonPathNode>.Rent();
                var collect = new BufferSink(next);
                foreach (var node in CollectionsMarshal.AsSpan(current))
                    Apply(Segments[i], node, context, ref collect, ref budget);
                BufferPool<JsonPathNode>.Return(current);
                current = next;
                if (current.Count == 0) return;
            }

            var last = Segments[^1];
            var selected = new SelectedSink<TSink>(sink);
            try
            {
                foreach (var node in CollectionsMarshal.AsSpan(current))
                {
                    if (!Apply(last, node, context, ref selected, ref budget)) return;
                }
            }
            finally
            {
                sink = selected.Inner;
            }
        }
        finally
        {
            BufferPool<JsonPathNode>.Return(current);
        }
    }

    private static bool Apply<TSink>(Segment segment, in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink => Select(segment.Selectors, node, context, ref sink, ref budget);

    private static bool Select<TSink>(Selector[] selectors, in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink
    {
        foreach (var selector in selectors)
        {
            budget.Step();
            var keepGoing = selector switch
            {
                NameSelector name => SelectName(name, node, context, ref sink, ref budget),
                WildcardSelector => SelectWildcard(node, context, ref sink, ref budget),
                IndexSelector index => SelectIndex(index, node, context, ref sink, ref budget),
                SliceSelector slice => SelectSlice(slice, node, context, ref sink, ref budget),
                _ => throw new InvalidOperationException("Unknown selector " + selector.GetType().Name),
            };
            if (!keepGoing) return false;
        }
        return true;
    }

    private static bool SelectName<TSink>(NameSelector name, in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink
    {
        if (node.Value.ValueKind != JsonValueKind.Object) return true;
        if (!JsonText.TryGetLastProperty(node.Value, name.Utf8Name, out var value)) return true;
        return sink.Add(new JsonPathNode(value, context.TrackPaths ? new PathLink(node.Path, name.Name) : null), ref budget);
    }

    private static bool SelectWildcard<TSink>(in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink
    {
        if (node.Value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var element in node.Value.EnumerateArray())
            {
                budget.Step();
                if (!sink.Add(new JsonPathNode(element, context.TrackPaths ? new PathLink(node.Path, i) : null), ref budget)) return false;
                i++;
            }
        }
        else if (node.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in node.Value.EnumerateObject())
            {
                budget.Step();
                var path = context.TrackPaths ? new PathLink(node.Path, JsonText.PropertyName(member)) : null;
                if (!sink.Add(new JsonPathNode(member.Value, path), ref budget)) return false;
            }
        }
        return true;
    }

    private static bool SelectIndex<TSink>(IndexSelector index, in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink
    {
        if (node.Value.ValueKind != JsonValueKind.Array) return true;
        long length = node.Value.GetArrayLength();
        var at = index.Index >= 0 ? index.Index : length + index.Index;
        if (at < 0 || at >= length) return true;
        return sink.Add(new JsonPathNode(node.Value[(int)at], context.TrackPaths ? new PathLink(node.Path, (int)at) : null), ref budget);
    }

    private static bool SelectSlice<TSink>(SliceSelector slice, in JsonPathNode node, in EvaluationContext context, ref TSink sink, ref JsonPathBudget budget)
        where TSink : struct, IBudgetedSink
    {
        if (node.Value.ValueKind != JsonValueKind.Array || slice.Step == 0) return true;
        long length = node.Value.GetArrayLength();
        var step = slice.Step;
        long lower, upper;
        if (step > 0)
        {
            var start = Normalize(slice.Start ?? 0, length);
            var end = Normalize(slice.End ?? length, length);
            lower = Math.Min(Math.Max(start, 0), length);
            upper = Math.Min(Math.Max(end, 0), length);
            long i = 0;
            foreach (var element in node.Value.EnumerateArray())
            {
                if (i >= upper) break;
                budget.Step();
                if (i >= lower && (i - lower) % step == 0
                    && !sink.Add(new JsonPathNode(element, context.TrackPaths ? new PathLink(node.Path, (int)i) : null), ref budget))
                    return false;
                i++;
            }
            return true;
        }

        var startDown = Normalize(slice.Start ?? length - 1, length);
        var endDown = Normalize(slice.End ?? -length - 1, length);
        upper = Math.Min(Math.Max(startDown, -1), length - 1);
        lower = Math.Min(Math.Max(endDown, -1), length - 1);
        if (upper <= lower) return true;

        budget.Steps(length);
        var elements = ArrayPool<JsonElement>.Shared.Rent((int)length);
        try
        {
            var n = 0;
            foreach (var element in node.Value.EnumerateArray()) elements[n++] = element;
            for (var i = upper; lower < i; i += step)
            {
                if (!sink.Add(new JsonPathNode(elements[i], context.TrackPaths ? new PathLink(node.Path, (int)i) : null), ref budget))
                    return false;
            }
            return true;
        }
        finally
        {
            ArrayPool<JsonElement>.Shared.Return(elements, clearArray: true);
        }

        static long Normalize(long index, long length) => index >= 0 ? index : length + index;
    }

    private readonly struct BufferSink(List<JsonPathNode> buffer) : IBudgetedSink
    {
        public bool Add(in JsonPathNode node, ref JsonPathBudget budget)
        {
            buffer.Add(node);
            return true;
        }
    }

    private struct SelectedSink<TSink>(TSink inner) : IBudgetedSink
        where TSink : struct, INodeSink
    {
        public TSink Inner = inner;

        public bool Add(in JsonPathNode node, ref JsonPathBudget budget)
        {
            budget.Select(node.Value);
            return Inner.Add(node);
        }
    }
}

internal readonly struct ElementListSink(List<JsonElement> items) : INodeSink
{
    public List<JsonElement> Items { get; } = items;

    public bool Add(in JsonPathNode node)
    {
        Items.Add(node.Value);
        return true;
    }
}

internal readonly struct PathListSink(List<(JsonElement, string)> items) : INodeSink
{
    public List<(JsonElement, string)> Items { get; } = items;

    public bool Add(in JsonPathNode node)
    {
        Items.Add((node.Value, PathLink.Format(node.Path)));
        return true;
    }
}

internal static class BufferPool<T>
{
    private const int MaxRetained = 16;
    private const int MaxRetainedCapacity = 4096;

    [ThreadStatic] private static List<T>[]? t_free;
    [ThreadStatic] private static int t_count;

    public static List<T> Rent()
    {
        if (t_count == 0) return new List<T>();
        var list = t_free![--t_count];
        t_free[t_count] = null!;
        return list;
    }

    public static void Return(List<T> list)
    {
        if (list.Capacity > MaxRetainedCapacity) return;
        list.Clear();
        t_free ??= new List<T>[MaxRetained];
        if (t_count < MaxRetained) t_free[t_count++] = list;
    }
}
