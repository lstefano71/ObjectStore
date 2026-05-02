namespace ObjectStore.Tests;

public class BTreeTests : IDisposable
{
    private readonly string _tempPath;
    private readonly ContainerFile _file;
    private readonly BuddyAllocator _allocator;
    private readonly BTree _tree;

    public BTreeTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"objstore_btree_{Guid.NewGuid():N}.bin");
        _file = ContainerFile.Create(_tempPath);
        _file.EnsureSize(FormatConstants.DataRegionOffset + 1024 * 1024); // 1MB for tests
        _allocator = new BuddyAllocator(_file, FormatConstants.DataRegionOffset);
        _tree = new BTree(_allocator, _file, order: 3); // Small order for testing splits
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(_tempPath);
    }

    [Fact]
    public void Insert_SingleKey_CanGet()
    {
        var key = new BTreeKey(1, 100);
        byte[] value = [1, 2, 3, 4];
        _tree.Insert(key, value);

        var result = _tree.Get(key);
        Assert.NotNull(result);
        Assert.Equal(value, result);
    }

    [Fact]
    public void Get_NonExistentKey_ReturnsNull()
    {
        var key = new BTreeKey(1, 999);
        Assert.Null(_tree.Get(key));
    }

    [Fact]
    public void Insert_MultipleKeys_AllRetrievable()
    {
        for (ulong i = 1; i <= 20; i++)
        {
            var key = new BTreeKey(1, i);
            _tree.Insert(key, BitConverter.GetBytes(i));
        }

        for (ulong i = 1; i <= 20; i++)
        {
            var key = new BTreeKey(1, i);
            var result = _tree.Get(key);
            Assert.NotNull(result);
            Assert.Equal(BitConverter.GetBytes(i), result);
        }
    }

    [Fact]
    public void Insert_DuplicateKey_Throws()
    {
        var key = new BTreeKey(1, 100);
        _tree.Insert(key, [1, 2, 3]);

        Assert.Throws<ObjectAlreadyExistsException>(() => _tree.Insert(key, [4, 5, 6]));
    }

    [Fact]
    public void Delete_ExistingKey_ReturnsTrue()
    {
        var key = new BTreeKey(1, 100);
        _tree.Insert(key, [1, 2, 3]);

        Assert.True(_tree.Delete(key));
        Assert.Null(_tree.Get(key));
    }

    [Fact]
    public void Delete_NonExistentKey_ReturnsFalse()
    {
        Assert.False(_tree.Delete(new BTreeKey(1, 999)));
    }

    [Fact]
    public void Update_ExistingKey_ChangesValue()
    {
        var key = new BTreeKey(1, 100);
        _tree.Insert(key, [1, 2, 3]);

        Assert.True(_tree.Update(key, [7, 8, 9]));
        var result = _tree.Get(key);
        Assert.Equal(new byte[] { 7, 8, 9 }, result);
    }

    [Fact]
    public void Update_NonExistentKey_ReturnsFalse()
    {
        Assert.False(_tree.Update(new BTreeKey(1, 999), [1]));
    }

    [Fact]
    public void Insert_ManyKeys_TriggersSplits()
    {
        // With order=3, max keys per node = 5. Insert enough to trigger multiple splits.
        for (ulong i = 1; i <= 50; i++)
        {
            _tree.Insert(new BTreeKey(1, i), BitConverter.GetBytes(i));
        }

        // All should be retrievable
        for (ulong i = 1; i <= 50; i++)
        {
            var result = _tree.Get(new BTreeKey(1, i));
            Assert.NotNull(result);
            Assert.Equal(BitConverter.GetBytes(i), result);
        }
    }

    [Fact]
    public void Delete_ManyKeys_TreeRemainsConsistent()
    {
        for (ulong i = 1; i <= 30; i++)
            _tree.Insert(new BTreeKey(1, i), BitConverter.GetBytes(i));

        // Delete even keys
        for (ulong i = 2; i <= 30; i += 2)
            Assert.True(_tree.Delete(new BTreeKey(1, i)));

        // Odd keys still there
        for (ulong i = 1; i <= 30; i += 2)
            Assert.NotNull(_tree.Get(new BTreeKey(1, i)));

        // Even keys gone
        for (ulong i = 2; i <= 30; i += 2)
            Assert.Null(_tree.Get(new BTreeKey(1, i)));
    }

    [Fact]
    public void RangeScan_ReturnsOnlyMatchingParentId()
    {
        // Insert keys under parent 1 and parent 2
        for (ulong i = 1; i <= 5; i++)
        {
            _tree.Insert(new BTreeKey(1, i), [(byte)i]);
            _tree.Insert(new BTreeKey(2, i), [(byte)(i + 100)]);
        }

        var parent1Results = _tree.RangeScan(1).ToList();
        Assert.Equal(5, parent1Results.Count);
        foreach (var (key, _) in parent1Results)
            Assert.Equal((ulong)1, key.ParentId);

        var parent2Results = _tree.RangeScan(2).ToList();
        Assert.Equal(5, parent2Results.Count);
        foreach (var (key, _) in parent2Results)
            Assert.Equal((ulong)2, key.ParentId);
    }

    [Fact]
    public void RangeScan_EmptyParent_ReturnsEmpty()
    {
        _tree.Insert(new BTreeKey(1, 1), [1]);
        var results = _tree.RangeScan(99).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void ScanAll_ReturnsAllInOrder()
    {
        var keys = new BTreeKey[]
        {
            new(1, 3), new(1, 1), new(2, 5), new(1, 2), new(2, 1),
        };

        foreach (var key in keys)
            _tree.Insert(key, []);

        var results = _tree.ScanAll().Select(kv => kv.Key).ToList();
        Assert.Equal(5, results.Count);

        // Verify ordering
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1] < results[i]);
    }

    [Fact]
    public void COW_ProducesNewRootAddress()
    {
        _tree.Insert(new BTreeKey(1, 1), [1]);
        long root1 = _tree.RootAddress;

        _tree.Insert(new BTreeKey(1, 2), [2]);
        long root2 = _tree.RootAddress;

        Assert.NotEqual(root1, root2);
    }

    [Fact]
    public void TryGet_ReturnsCorrectBoolean()
    {
        _tree.Insert(new BTreeKey(1, 1), [42]);

        Assert.True(_tree.TryGet(new BTreeKey(1, 1), out var val));
        Assert.Equal(new byte[] { 42 }, val);

        Assert.False(_tree.TryGet(new BTreeKey(1, 999), out _));
    }
}
