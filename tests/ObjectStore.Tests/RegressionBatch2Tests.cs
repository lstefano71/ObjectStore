namespace ObjectStore.Tests;

/// <summary>
/// Regression tests for code review findings — Batch 2 (crash safety &amp; allocator).
/// Issue 5: Buddy state block leaked on every commit
/// Issue 6: Allocator serialized before its own storage allocation
/// Issue 7: No fsync before superblock write
/// Issue 11: Free lists now purely in-memory (no disk mutations during alloc/free)
/// </summary>
public class RegressionBatch2Tests : IDisposable
{
    private readonly string _path;
    private ObjectEngine? _engine;

    public RegressionBatch2Tests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_regr2_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        _engine?.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private ObjectEngine CreateEngine()
    {
        _engine = ObjectEngine.Create(_path);
        return _engine;
    }

    // --- Issue 5: Buddy state block leak ---

    [Fact]
    public void ManyCommits_FileDoesNotGrowUnbounded()
    {
        var engine = CreateEngine();

        // Warm up: create some objects to establish baseline file size
        for (int i = 0; i < 20; i++)
        {
            ulong id = engine.CreateObject($"warmup_{i}");
            engine.DeleteObject(id);
        }

        long baselineSize = engine.File.FileSize;

        // Do 200 more create/delete cycles — file should stabilize, not keep growing
        for (int i = 0; i < 200; i++)
        {
            ulong id = engine.CreateObject($"cycle_{i}");
            engine.DeleteObject(id);
        }

        long finalSize = engine.File.FileSize;

        // File should not grow more than 50% beyond baseline (freed buddy blocks reused)
        Assert.True(finalSize <= baselineSize * 3 / 2,
            $"File grew from {baselineSize} to {finalSize} after 200 create/delete cycles — likely leaking");
    }

    [Fact]
    public void CreateDeleteLoop_FreeSpaceEventuallyReclaimed()
    {
        var engine = CreateEngine();

        // Create objects that use real data (extents)
        var ids = new List<ulong>();
        for (int i = 0; i < 50; i++)
        {
            ulong id = engine.CreateObject($"obj_{i}");
            engine.Append(id, new byte[100]);
            ids.Add(id);
        }

        long sizeAfterCreate = engine.File.FileSize;

        // Delete all
        foreach (var id in ids)
            engine.DeleteObject(id);

        // Allocate new objects — they should reuse freed space
        for (int i = 0; i < 50; i++)
        {
            ulong id = engine.CreateObject($"reuse_{i}");
            engine.Append(id, new byte[100]);
        }

        long sizeAfterReuse = engine.File.FileSize;

        // File should not have grown significantly — space was reused
        Assert.True(sizeAfterReuse <= sizeAfterCreate * 3 / 2,
            $"After delete+recreate: grew from {sizeAfterCreate} to {sizeAfterReuse} — space not reused");
    }

    // --- Issue 6: Allocator state consistency after reopen ---

    [Fact]
    public void CommitAndReopen_AllocatorStateConsistent()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("persist-test");
            byte[] data = new byte[100];
            Random.Shared.NextBytes(data);
            engine.Append(id, data);
        }

        // Reopen and verify object is accessible (allocator state was correct)
        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
            var info = engine.GetInfo(id);
            Assert.Equal(100, info!.Size);

            // Create another object — should allocate without hitting the buddy block
            ulong id2 = engine.CreateObject("after-reopen");
            Assert.True(engine.Exists(id2));
        }

        // Reopen again — both objects should exist
        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
        }
    }

    [Fact]
    public void ManyAllocations_CommitReopen_NoDoubleAllocation()
    {
        var ids = new List<ulong>();

        using (var engine = ObjectEngine.Create(_path))
        {
            // Create many objects to exercise the allocator
            for (int i = 0; i < 50; i++)
            {
                ulong id = engine.CreateObject($"alloc_test_{i}");
                engine.Append(id, new byte[64]); // force extent allocation
                ids.Add(id);
            }
        }

        // Reopen and verify all objects with their data
        using (var engine = ObjectEngine.Open(_path))
        {
            foreach (var id in ids)
            {
                Assert.True(engine.Exists(id));
                byte[] buf = new byte[64];
                int read = engine.ReadAt(id, 0, buf);
                Assert.Equal(64, read);
            }

            // Allocate more — should not collide with existing
            ulong newId = engine.CreateObject("no-collision");
            engine.Append(newId, new byte[128]);
            Assert.True(engine.Exists(newId));
        }
    }

    // --- Issue 11: In-memory free lists ---

    [Fact]
    public void AllocateAndFree_NoIntermediateDiskCorruption()
    {
        var engine = CreateEngine();

        // Allocate several blocks
        long addr1 = engine.Allocator.Allocate(0); // 64 bytes
        long addr2 = engine.Allocator.Allocate(0);
        long addr3 = engine.Allocator.Allocate(0);

        // Write known data to them
        byte[] marker = [0xDE, 0xAD, 0xBE, 0xEF];
        engine.File.WriteRaw(addr1 + FormatConstants.BlockHeaderSize, marker);
        engine.File.WriteRaw(addr2 + FormatConstants.BlockHeaderSize, marker);
        engine.File.WriteRaw(addr3 + FormatConstants.BlockHeaderSize, marker);

        // Free them — should NOT corrupt the data on disk (no next-pointer writes)
        engine.Allocator.Free(addr1, 0);
        engine.Allocator.Free(addr2, 0);
        engine.Allocator.Free(addr3, 0);

        // Read back — marker data should still be intact (not overwritten by free-list pointer)
        Span<byte> readBuf = stackalloc byte[4];
        engine.File.ReadRaw(addr1 + FormatConstants.BlockHeaderSize, readBuf);
        Assert.Equal(marker, readBuf.ToArray());

        engine.File.ReadRaw(addr2 + FormatConstants.BlockHeaderSize, readBuf);
        Assert.Equal(marker, readBuf.ToArray());
    }

    [Fact]
    public void StressAllocFree_CommitReopen_Consistent()
    {
        ulong objId;
        using (var engine = ObjectEngine.Create(_path))
        {
            // Stress: many allocations and frees
            var addrs = new List<(long, int)>();
            for (int i = 0; i < 100; i++)
            {
                long addr = engine.Allocator.Allocate(0);
                addrs.Add((addr, 0));
            }

            // Free half
            for (int i = 0; i < 50; i++)
                engine.Allocator.Free(addrs[i].Item1, addrs[i].Item2);

            // Create an object (triggers auto-commit, persisting allocator state)
            objId = engine.CreateObject("stress-obj");
            engine.Append(objId, new byte[32]);
        }

        // Reopen and verify consistency
        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(objId));
            Assert.True(engine.Allocator.FreeBlockCount > 0);

            // Should be able to allocate from free list
            long newAddr = engine.Allocator.Allocate(0);
            Assert.True(newAddr >= FormatConstants.DataRegionOffset);
        }
    }

    // --- Multi-writer support: RefreshFromDisk ---

    [Fact]
    public void RefreshFromDisk_SeesNewState()
    {
        // Simulate: writer commits, then we refresh and see updates
        var engine = CreateEngine();

        // Create initial state
        ulong id1 = engine.CreateObject("before-refresh");
        engine.Append(id1, new byte[50]);

        // Capture state before
        long treeRootBefore = engine.Tree.RootAddress;

        // Create more objects (auto-commits)
        ulong id2 = engine.CreateObject("after-create");

        // The tree root should have changed
        Assert.NotEqual(treeRootBefore, engine.Tree.RootAddress);

        // RefreshFromDisk should reload to the latest committed state
        engine.RefreshFromDisk();

        // After refresh, both objects should still be accessible
        Assert.True(engine.Exists(id1));
        Assert.True(engine.Exists(id2));
    }
}
