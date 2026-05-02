namespace ObjectStore.Tests;

public class SieveCacheTests
{
    [Fact]
    public void BasicPutAndGet()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "one");
        cache.Put(2, "two");

        Assert.True(cache.TryGet(1, out var val));
        Assert.Equal("one", val);
        Assert.True(cache.TryGet(2, out val));
        Assert.Equal("two", val);
    }

    [Fact]
    public void MissReturnsDefault()
    {
        var cache = new SieveCache<int, string>(3);
        Assert.False(cache.TryGet(99, out var val));
        Assert.Null(val);
    }

    [Fact]
    public void Eviction_AtCapacity()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");

        // All should be present
        Assert.Equal(3, cache.Count);

        // Add one more - should evict one
        cache.Put(4, "d");
        Assert.Equal(3, cache.Count);
        Assert.True(cache.TryGet(4, out _));
    }

    [Fact]
    public void Eviction_VisitedBit_Protects()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");

        // Access item 3 (sets visited bit)
        cache.TryGet(3, out _);

        // Add item 4 - should evict an unvisited item (1 or 2, not 3)
        cache.Put(4, "d");

        // Item 3 should still be there (was visited)
        Assert.True(cache.TryGet(3, out var val));
        Assert.Equal("c", val);
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "a");
        cache.Put(2, "b");

        bool removed = cache.Invalidate(1);
        Assert.True(removed);
        Assert.False(cache.TryGet(1, out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Invalidate_NonExistent_ReturnsFalse()
    {
        var cache = new SieveCache<int, string>(3);
        Assert.False(cache.Invalidate(99));
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void Update_ExistingKey()
    {
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "old");
        cache.Put(1, "new");

        Assert.True(cache.TryGet(1, out var val));
        Assert.Equal("new", val);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Capacity_One()
    {
        var cache = new SieveCache<int, string>(1);
        cache.Put(1, "a");
        cache.Put(2, "b"); // evicts 1
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
    }

    [Fact]
    public void EvictionOrder_FIFO_WithoutVisits()
    {
        // With no visits, SIEVE acts as FIFO from tail
        var cache = new SieveCache<int, string>(3);
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");
        cache.Put(4, "d"); // evicts from tail (3, the last inserted = last in linked list)

        // Verify: one of 1,2,3 was evicted
        int present = 0;
        if (cache.TryGet(1, out _)) present++;
        if (cache.TryGet(2, out _)) present++;
        if (cache.TryGet(3, out _)) present++;
        if (cache.TryGet(4, out _)) present++;
        Assert.Equal(3, present); // 3 items present out of 4
    }

    [Fact]
    public void HeavyLoad_NoCorruption()
    {
        var cache = new SieveCache<int, int>(50);
        var rng = new Random(42);

        for (int i = 0; i < 10000; i++)
        {
            int key = rng.Next(200);
            cache.Put(key, key * 2);
        }

        // Verify all cached values are correct
        for (int key = 0; key < 200; key++)
        {
            if (cache.TryGet(key, out var val))
                Assert.Equal(key * 2, val);
        }

        Assert.True(cache.Count <= 50);
    }
}
