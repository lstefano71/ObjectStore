namespace ObjectStore.Tests;

/// <summary>
/// Regression tests for Batch 4 fixes:
/// - Fix 4: Hash collision handling in primary tree
/// - Fix 8: Options wired into C API (ReadOnly)
/// - Fix 10: Reject gap writes (offset > size)
/// </summary>
public class RegressionBatch4Tests : IDisposable
{
    private readonly string _dir;

    public RegressionBatch4Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"objstore_batch4_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewFile() => Path.Combine(_dir, $"{Guid.NewGuid():N}.obs");

    // ===== Fix 4: Hash Collision Handling =====

    [Fact]
    public void HashCollision_BothObjectsInsertable()
    {
        // Simulate a collision by pre-inserting a record at the key slot
        // that CreateObject would use for a different name
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        // Create first object with a name
        ulong id1 = engine.CreateObject("alpha");

        // Now manually insert a fake record at the same key slot (parentId=1, same nameHash)
        // that a second object "beta" would try to use — but with a different name.
        // We'll force this by getting the hash of "beta" and pre-inserting at that key.
        ulong betaHash = FnvHash.ComputeString("beta");
        var fakeKey = new BTreeKey(1, betaHash);
        var fakeRecord = new NodeRecord
        {
            Id = 99999,
            ParentId = 1,
            NameHash = betaHash,
            Name = "gamma", // Different name but same hash slot
            NodeTypeFlags = NodeRecord.FlagHasData,
            Size = 0,
            Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        engine.PrimaryTree.Insert(fakeKey, fakeRecord.Serialize());
        engine.CommitInternal();

        // Now create an object named "beta" — should hit collision and use fallback key
        ulong id2 = engine.CreateObject("beta");

        // Both should be findable by ID
        var info1 = engine.GetInfo(id1);
        var info2 = engine.GetInfo(id2);
        Assert.NotNull(info1);
        Assert.NotNull(info2);
        Assert.Equal("alpha", info1!.Name);
        Assert.Equal("beta", info2!.Name);
    }

    [Fact]
    public void HashCollision_GenuineDuplicate_Throws()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        // Create object named "alpha"
        ulong id1 = engine.CreateObject("alpha");

        // Pre-insert a record at the same key with the SAME name "test_dup" under same parent
        ulong dupHash = FnvHash.ComputeString("test_dup");
        var fakeKey = new BTreeKey(1, dupHash);
        var fakeRecord = new NodeRecord
        {
            Id = 99998,
            ParentId = 1,
            NameHash = dupHash,
            Name = "test_dup",
            NodeTypeFlags = NodeRecord.FlagHasData,
            Size = 0,
            Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        engine.PrimaryTree.Insert(fakeKey, fakeRecord.Serialize());
        engine.CommitInternal();

        // Now creating another object with the same name should throw
        Assert.Throws<ObjectAlreadyExistsException>(() => engine.CreateObject("test_dup"));
    }

    [Fact]
    public void HashCollision_FallbackKey_SurvivesReopenAndFindById()
    {
        var path = NewFile();
        ulong id2;

        using (var engine = ObjectEngine.OpenOrCreate(path))
        {
            // Pre-occupy the key slot for "collider"
            ulong colliderHash = FnvHash.ComputeString("collider");
            var fakeKey = new BTreeKey(1, colliderHash);
            var fakeRecord = new NodeRecord
            {
                Id = 99997,
                ParentId = 1,
                NameHash = colliderHash,
                Name = "occupant", // Different name, same hash key
                NodeTypeFlags = NodeRecord.FlagHasData,
                Size = 0,
                Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            engine.PrimaryTree.Insert(fakeKey, fakeRecord.Serialize());
            engine.CommitInternal();

            // Create "collider" — collision, will use fallback key
            id2 = engine.CreateObject("collider");
        }

        // Reopen and verify FindById still works
        using (var engine = ObjectEngine.OpenReadOnly(path))
        {
            var info = engine.GetInfo(id2);
            Assert.NotNull(info);
            Assert.Equal("collider", info!.Name);
        }
    }

    // ===== Fix 10: Reject Gap Writes =====

    [Fact]
    public void WriteAt_GapBeyondSize_Throws()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("gaptest");
        engine.Append(id, new byte[100]);

        // Write at offset 200 (beyond size=100) should fail
        Assert.Throws<ArgumentException>(() =>
            engine.WriteAt(id, 200, new byte[10]));
    }

    [Fact]
    public void WriteAt_AtExactSize_Throws()
    {
        // WriteAt at offset == size is also invalid (use Append for that)
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("exact");
        engine.Append(id, new byte[50]);

        // WriteAt at offset=50 (== size) should throw since there's nothing to overwrite
        Assert.Throws<ArgumentException>(() =>
            engine.WriteAt(id, 50, new byte[10]));
    }

    [Fact]
    public void WriteAt_OverwriteWithinSize_Succeeds()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("overwrite");
        engine.Append(id, new byte[100]);

        // Write at offset 50 (within size) should succeed
        var data = new byte[20];
        Array.Fill(data, (byte)0xAB);
        engine.WriteAt(id, 50, data);

        var readBack = new byte[100];
        engine.ReadAt(id, 0, readBack);
        Assert.Equal(0xAB, readBack[50]);
        Assert.Equal(0xAB, readBack[69]);
        Assert.Equal(0, readBack[70]);
    }

    [Fact]
    public void Append_AtSize_Succeeds()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("append");
        engine.Append(id, new byte[50]);

        // Appending (which adds at offset == size) should work
        var extra = new byte[25];
        Array.Fill(extra, (byte)0xCD);
        engine.Append(id, extra);

        var info = engine.GetInfo(id);
        Assert.Equal(75L, info!.Size);
    }

    // ===== Fix 8: Options Wired (ReadOnly) =====

    [Fact]
    public void ReadOnly_RejectsModification()
    {
        var path = NewFile();

        // Create a file first
        using (var engine = ObjectEngine.OpenOrCreate(path))
        {
            engine.CreateObject("test");
        }

        // Open read-only
        using var ro = ObjectEngine.OpenReadOnly(path);
        Assert.True(ro.IsReadOnly);

        Assert.Throws<ReadOnlyContainerException>(() => ro.CreateObject("fail"));
    }

    [Fact]
    public void ReadOnly_CanReadExistingData()
    {
        var path = NewFile();
        ulong id;

        using (var engine = ObjectEngine.OpenOrCreate(path))
        {
            id = engine.CreateObject("readable");
            engine.Append(id, "hello"u8.ToArray());
        }

        using var ro = ObjectEngine.OpenReadOnly(path);
        var info = ro.GetInfo(id);
        Assert.NotNull(info);
        Assert.Equal("readable", info!.Name);
        Assert.Equal(5L, info.Size);

        var buf = new byte[5];
        ro.ReadAt(id, 0, buf);
        Assert.Equal("hello"u8.ToArray(), buf);
    }
}
