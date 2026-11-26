using System.Collections.Concurrent;

namespace ReeveWorkflow.Core.Expressions;

internal static class ExpressionSyntaxCache
{
    private const int MaxCachedSources = 4096;
    private static readonly ExpressionParseCache ParsedSources = new(MaxCachedSources);

    internal sealed class ExpressionParseCache(int capacity)
    {
        private readonly int _capacity = GuardCapacity(capacity);
        private readonly ConcurrentDictionary<string, CachedExpression> _entries = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private long _clock;

        private static int GuardCapacity(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity, nameof(capacity));
            return capacity;
        }

        internal int Count => _entries.Count;

        public ExpressionNode GetOrParse(string source)
        {
            if (_entries.TryGetValue(source, out var cached))
            {
                cached.Touch(NextStamp());
                return cached.Node;
            }

            var parsed = ExpressionParser.Parse(source);
            lock (_gate)
            {
                if (_entries.TryGetValue(source, out var existing))
                {
                    existing.Touch(NextStamp());
                    return existing.Node;
                }

                if (_entries.Count >= _capacity)
                {
                    var count = _entries.Count;
                    var toEvict = Math.Max(1, count / 8);

                    var stamps = new long[count];
                    var keys = new string[count];
                    int i = 0;
                    foreach (var pair in _entries)
                    {
                        stamps[i] = pair.Value.LastAccess;
                        keys[i] = pair.Key;
                        i++;
                    }

                    Array.Sort(stamps, keys);

                    for (int j = 0; j < toEvict && j < count; j++)
                    {
                        _entries.TryRemove(keys[j], out _);
                    }
                }

                _entries.TryAdd(source, new CachedExpression(parsed, NextStamp()));
                return parsed;
            }
        }

        private long NextStamp() => Interlocked.Increment(ref _clock);

        private sealed class CachedExpression(ExpressionNode node, long lastAccess)
        {
            private long _lastAccess = lastAccess;

            public ExpressionNode Node { get; } = node;
            public long LastAccess => Volatile.Read(ref _lastAccess);
            public void Touch(long stamp)
            {
                var previous = Volatile.Read(ref _lastAccess);
                while (stamp > previous)
                {
                    var observed = Interlocked.CompareExchange(ref _lastAccess, stamp, previous);
                    if (observed == previous)
                        return;
                    previous = observed;
                }
            }
        }
    }

    internal static ExpressionNode ParseCached(string source)
    {
        return ParsedSources.GetOrParse(source);
    }
}
