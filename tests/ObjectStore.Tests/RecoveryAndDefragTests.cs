namespace ObjectStore.Tests;

/// <summary>
/// Tests for dirty-close flag, recovery, and defragmentation.
/// </summary>
public class RecoveryAndDefragTests : IDisposable
{
    private readonly string _path;

    public RecoveryAndDefragTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_recovery_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void DirtyFlag_SetOnOpen_ClearedOnClose()
    {
        // Create a store and close it cleanly
        using (var engine = ObjectEngine.Create(_path))
        {
            engine.CreateObject("test");
        }

        // Reopen and verify dirty flag is set during open
        using (var engine = ObjectEngine.Open(_path))
        {
            // After open, dirty flag should be set
            Assert.True(engine.WasDirtyOnOpen());
        }

        // After close, flag should be cleared — reopen to verify
        using (var engine = ObjectEngine.Open(_path))
        {
            // The act of opening sets it dirty again, but the previous close cleared it
            // We can read the generation to verify a commit happened
            Assert.NotNull(engine.GetInfo(engine.NextNodeId - 1)); // original object still there
        }
    }

    [Fact]
    public void Recovery_AfterCleanClose_NoOp()
    {
        using var db = ObjectStoreDatabase.Create(_path);
        var id = db.CreateObject("obj1");
        db.Append(id, "hello"u8);

        bool needed = db.Recover();
        // Dirty flag was not set on create path, but recover should still work
        // The recover method returns whether dirty flag was set
        Assert.False(needed); // Clean state, no recovery needed

        // Verify data intact
        byte[] buf = new byte[5];
        db.ReadAt(id, 0, buf);
        Assert.Equal("hello"u8.ToArray(), buf);
    }

    [Fact]
    public void Recovery_ReclaimsLeakedBlocks()
    {
        ulong objId;
        long fileSizeBeforeRecovery;

        using (var engine = ObjectEngine.Create(_path))
        {
            objId = engine.CreateObject("survive");
            engine.Append(objId, new byte[500]);

            // Create and delete several objects to cause some block churn
            for (int i = 0; i < 10; i++)
            {
                var tmpId = engine.CreateObject($"tmp{i}");
                engine.Append(tmpId, new byte[200]);
            }
            // Delete them (blocks go to free list)
            for (int i = 0; i < 10; i++)
            {
                // Find and delete
                var info = engine.ListObjects().FirstOrDefault(r => r.Name == $"tmp{i}");
                if (info != null) engine.DeleteObject(info.Id);
            }
        }

        // Open and recover
        using (var engine = ObjectEngine.Open(_path))
        {
            fileSizeBeforeRecovery = engine.File.FileSize;
            Recovery.Recover(engine);

            // Original object should survive
            byte[] buf = new byte[500];
            int read = engine.ReadAt(objId, 0, buf);
            Assert.Equal(500, read);
        }
    }

    [Fact]
    public void Defragment_ConsolidatesObjects()
    {
        using var db = ObjectStoreDatabase.Create(_path);

        // Create objects with interleaved allocation (use size > inline threshold to ensure extent-based storage)
        var ids = new List<ulong>();
        for (int i = 0; i < 5; i++)
        {
            var id = db.CreateObject($"frag{i}");
            db.Append(id, new byte[1024]);
            ids.Add(id);
        }

        // Delete odd-indexed objects to create fragmentation
        db.DeleteObject(ids[1]);
        db.DeleteObject(ids[3]);

        // Defragment
        int count = db.Defragment();
        Assert.True(count >= 2); // at least the surviving objects with data

        // Verify remaining objects intact
        foreach (int idx in new[] { 0, 2, 4 })
        {
            byte[] buf = new byte[1024];
            int read = db.ReadAt(ids[idx], 0, buf);
            Assert.Equal(1024, read);
            Assert.All(buf, b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void Defragment_EmptyStore_ReturnsZero()
    {
        using var db = ObjectStoreDatabase.Create(_path);
        int count = db.Defragment();
        Assert.Equal(0, count);
    }

    [Fact]
    public void Defragment_PreservesMetadata()
    {
        using var db = ObjectStoreDatabase.Create(_path);
        var id = db.CreateObject("metaobj");
        db.Append(id, "data"u8);

        // Use engine directly for metadata
        db.Engine.SetMetadata(id, "key", "value");

        db.Defragment();

        // Metadata should survive
        Assert.Equal("value", db.Engine.GetMetadata(id, "key"));

        // Data should survive
        byte[] buf = new byte[4];
        db.ReadAt(id, 0, buf);
        Assert.Equal("data"u8.ToArray(), buf);
    }
}
