using FluentAssertions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionSyntaxCacheTests
{
    private static string Unique() => $"1 + {Random.Shared.NextInt64():D}";

    [Fact]
    public void The_same_source_is_parsed_once()
    {
        var source = Unique();

        var first = ExpressionSyntaxCache.ParseCached(source);
        var second = ExpressionSyntaxCache.ParseCached(source);

        second.Should().BeSameAs(first, "a second parse of a source already seen is work nobody asked for");
    }

    [Fact]
    public void Two_different_sources_are_two_trees()
    {
        ExpressionSyntaxCache.ParseCached(Unique())
            .Should().NotBeSameAs(ExpressionSyntaxCache.ParseCached(Unique()));
    }

    [Fact]
    public void A_source_that_does_not_parse_is_refused_every_time_it_is_offered()
    {
        var source = $"1 + + + {Random.Shared.Next()}";

        var first = () => ExpressionSyntaxCache.ParseCached(source);
        var second = () => ExpressionSyntaxCache.ParseCached(source);

        first.Should().Throw<ExpressionSyntaxException>();
        second.Should().Throw<ExpressionSyntaxException>();
    }

    [Fact]
    public void New_sources_after_capacity_are_cached_and_evict_the_least_recent_source()
    {
        var cache = new ExpressionSyntaxCache.ExpressionParseCache(4096);
        var sources = Enumerable.Range(0, 4097).Select(i => $"{i} + 0").ToArray();
        var trees = sources.Take(4096).Select(cache.GetOrParse).ToArray();

        var newest = cache.GetOrParse(sources[4096]);

        cache.GetOrParse(sources[4096]).Should().BeSameAs(newest);
        cache.GetOrParse(sources[1]).Should().NotBeSameAs(trees[1]);
        cache.GetOrParse(sources[2048]).Should().BeSameAs(trees[2048]);
        cache.GetOrParse(sources[0]).Should().NotBeSameAs(trees[0]);
    }

    [Fact]
    public void A_hot_source_survives_a_full_turnover_of_the_cache()
    {
        var cache = new ExpressionSyntaxCache.ExpressionParseCache(4096);
        var hotSource = Unique();
        var hotTree = cache.GetOrParse(hotSource);

        for (var i = 0; i < 4096; i++)
        {
            cache.GetOrParse($"{i} * 3 + 1");
            cache.GetOrParse(hotSource);
        }

        cache.GetOrParse(hotSource).Should().BeSameAs(hotTree);
    }

    [Fact]
    public async Task Concurrent_hits_and_misses_return_valid_cached_trees()
    {
        var hotSource = Unique();
        var hotTree = ExpressionSyntaxCache.ParseCached(hotSource);

        var results = await Task.WhenAll(Enumerable.Range(0, 512).Select(i => Task.Run(() =>
        {
            ExpressionSyntaxCache.ParseCached($"{i + 10000} + 7");
            return ExpressionSyntaxCache.ParseCached(hotSource);
        })));

        results.Should().OnlyContain(tree => ReferenceEquals(tree, hotTree));
    }

    [Fact]
    public void Parsing_five_thousand_distinct_sources_keeps_the_most_recent_at_capacity()
    {
        var cache = new ExpressionSyntaxCache.ExpressionParseCache(4096);
        var sources = Enumerable.Range(0, 5000).Select(i => $"{i} + 1").ToArray();

        var maxCount = 0;
        var treesByIndex = new Dictionary<int, ExpressionNode>();
        foreach (var i in Enumerable.Range(0, 5000))
        {
            treesByIndex[i] = cache.GetOrParse(sources[i]);
            maxCount = Math.Max(maxCount, cache.Count);
        }

        cache.Count.Should().BeLessThanOrEqualTo(4096, "cache should not exceed capacity");
        maxCount.Should().BeLessThanOrEqualTo(4096, "count should never exceed capacity");

        for (int i = 5000 - cache.Count; i < 5000; i++)
        {
            cache.GetOrParse(sources[i]).Should().BeSameAs(treesByIndex[i],
                $"source {i} should still be cached (most recent 4096)");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cache_rejects_non_positive_capacity(int capacity)
    {
        var act = () => new ExpressionSyntaxCache.ExpressionParseCache(capacity);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("capacity");
    }
}
