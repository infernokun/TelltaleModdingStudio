using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class SizeBoundedCacheTests
{
    private static SizeBoundedCache<string, byte[]?> Cache(long maxBytes) => new(maxBytes, v => v!.Length);

    [Fact]
    public void Drops_the_least_recently_used_entries_over_budget()
    {
        var cache = Cache(30);
        cache["a"] = new byte[10];
        cache["b"] = new byte[10];
        cache["c"] = new byte[10];
        cache["d"] = new byte[10];

        Assert.False(cache.TryGetValue("a", out _));
        Assert.True(cache.TryGetValue("b", out _));
        Assert.Equal(30, cache.SizeBytes);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Reading_an_entry_keeps_it()
    {
        var cache = Cache(30);
        cache["a"] = new byte[10];
        cache["b"] = new byte[10];
        cache["c"] = new byte[10];
        Assert.True(cache.TryGetValue("a", out _));

        cache["d"] = new byte[10];

        Assert.True(cache.TryGetValue("a", out _));
        Assert.False(cache.TryGetValue("b", out _));
    }

    [Fact]
    public void An_entry_larger_than_the_budget_stays_until_the_next_one()
    {
        var cache = Cache(10);
        cache["big"] = new byte[50];
        Assert.True(cache.TryGetValue("big", out var big));
        Assert.Equal(50, big!.Length);

        cache["small"] = new byte[5];

        Assert.False(cache.TryGetValue("big", out _));
        Assert.True(cache.TryGetValue("small", out _));
        Assert.Equal(5, cache.SizeBytes);
    }

    [Fact]
    public void Null_values_are_cached_and_cost_nothing()
    {
        var cache = Cache(10);
        cache["missing"] = null;

        Assert.True(cache.TryGetValue("missing", out var value));
        Assert.Null(value);
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public void Replacing_an_entry_updates_its_size()
    {
        var cache = Cache(100);
        cache["a"] = new byte[40];
        cache["a"] = new byte[10];

        Assert.Equal(10, cache.SizeBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Keys_follow_the_comparer()
    {
        var cache = new SizeBoundedCache<string, byte[]?>(100, v => v!.Length, StringComparer.OrdinalIgnoreCase);
        cache["Texture.d3dtx"] = new byte[1];

        Assert.True(cache.TryGetValue("texture.D3DTX", out _));
    }

    [Fact]
    public void Parallel_GetOrAdd_keeps_the_size_consistent_and_within_budget()
    {
        var cache = Cache(1000);
        int made = 0;
        Parallel.For(0, 20000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var value = cache.GetOrAdd($"k{i % 300}", _ =>
            {
                Interlocked.Increment(ref made);
                return new byte[10];
            });
            Assert.Equal(10, value!.Length);
        });

        Assert.True(cache.SizeBytes <= 1000);
        Assert.Equal(cache.Count * 10L, cache.SizeBytes);
        Assert.True(made >= 300);
    }

    [Fact]
    public void GetOrAdd_returns_the_cached_value_without_making_it_again()
    {
        var cache = Cache(100);
        int made = 0;
        var first = cache.GetOrAdd("a", _ => { made++; return new byte[5]; });
        var second = cache.GetOrAdd("a", _ => { made++; return new byte[5]; });

        Assert.Same(first, second);
        Assert.Equal(1, made);
    }

    [Fact]
    public void Budgets_scale_with_memory_within_bounds()
    {
        long budget = CacheBudget.For(1 / 16.0, 256L << 20, 1L << 30);

        Assert.InRange(budget, 256L << 20, 1L << 30);
    }
}
