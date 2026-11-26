using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ReeveWorkflow.Abstractions;

internal static class PortMaps
{
    internal const string ReadsRuntimeShape =
        "A map is recognised by the IReadOnlyDictionary<string, T> its runtime type implements, which trimming may remove.";

    internal const string InstantiatesRuntimeShape =
        "A map's entries are read through a generic method instantiated over its runtime value type.";

    private static readonly ConcurrentDictionary<Type, Func<object, IEnumerable<KeyValuePair<string, object?>>>?> Readers = new();

    private static readonly MethodInfo EntriesMethod =
        typeof(PortMaps).GetMethod(nameof(EntriesOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    [RequiresUnreferencedCode(ReadsRuntimeShape)]
    [RequiresDynamicCode(InstantiatesRuntimeShape)]
    internal static bool TryEntries(object? value, [NotNullWhen(true)] out IEnumerable<KeyValuePair<string, object?>>? entries)
    {
        entries = null;
        if (value is null or string)
            return false;
        var reader = Readers.GetOrAdd(value.GetType(), ReaderOf);
        entries = reader?.Invoke(value);
        return entries is not null;
    }

    [RequiresUnreferencedCode(ReadsRuntimeShape)]
    [RequiresDynamicCode(InstantiatesRuntimeShape)]
    internal static bool IsMap(object? value) => value is not (null or string) && Readers.GetOrAdd(value.GetType(), ReaderOf) is not null;

    [RequiresUnreferencedCode(ReadsRuntimeShape)]
    [RequiresDynamicCode(InstantiatesRuntimeShape)]
    private static Func<object, IEnumerable<KeyValuePair<string, object?>>>? ReaderOf(Type type)
    {
        var dictionary = (type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces()).FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) && i.GetGenericArguments()[0] == typeof(string));
        return dictionary is null
            ? null
            : (Func<object, IEnumerable<KeyValuePair<string, object?>>>)Delegate.CreateDelegate(
                typeof(Func<object, IEnumerable<KeyValuePair<string, object?>>>),
                EntriesMethod.MakeGenericMethod(dictionary.GetGenericArguments()[1]));
    }

    private static IEnumerable<KeyValuePair<string, object?>> EntriesOf<T>(object map)
    {
        foreach (var entry in (IReadOnlyDictionary<string, T>)map)
            yield return new KeyValuePair<string, object?>(entry.Key, entry.Value);
    }
}

internal sealed class MapValue<T> : IReadOnlyDictionary<string, T>
{
    private readonly KeyValuePair<string, T>[] entries;
    private readonly Dictionary<string, int> positions;

    internal MapValue(KeyValuePair<string, T>[] entries, Dictionary<string, int> positions)
    {
        this.entries = entries;
        this.positions = positions;
    }

    public T this[string key] => positions.TryGetValue(key, out var at) ? entries[at].Value : throw new KeyNotFoundException();

    public IEnumerable<string> Keys => entries.Select(e => e.Key);

    public IEnumerable<T> Values => entries.Select(e => e.Value);

    public int Count => entries.Length;

    public bool ContainsKey(string key) => positions.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value)
    {
        if (positions.TryGetValue(key, out var at))
        {
            value = entries[at].Value;
            return true;
        }

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, T>>)entries).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
