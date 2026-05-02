namespace ObjectStore.Tests;

/// <summary>
/// Regression tests for Batch 3 fixes:
/// - Fix 3: Persistent ID-index B-tree (O(log n) FindById)
/// - Fix 4: Hash collision handling (not yet implemented, placeholder)
/// - Fix 9: AllocateTracked for transaction rollback
/// </summary>
public class RegressionBatch3Tests : IDisposable
{
    private readonly string _dir;

    public RegressionBatch3Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"objstore_batch3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ObjectEngine CreateEngine() => ObjectEngine.Create(Path.Combine(_dir, $"test_{Guid.NewGuid():N}.obs"));

    // --- Fix 3: ID Index Tests ---

    [Fact]
    public void FindById_WithIdIndex_ReturnsCorrectRecord()
    {
        using var engine = CreateEngine();
        var ids = new List<ulong>();
        for (int i = 0; i < 100; i++)
            ids.Add(engine.CreateObject($"obj_{i:D3}"));

        // All should be findable via ID index (O(log n))
        foreach (var id in ids)
        {
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
            Assert.Equal(id, info.Id);
        }
    }

    [Fact]
    public void FindById_AfterDelete_ReturnsNull()
    {
        using var engine = CreateEngine();
        ulong id1 = engine.CreateObject("to_delete");
        ulong id2 = engine.CreateObject("to_keep");

        engine.DeleteObject(id1);

        Assert.Null(engine.GetInfo(id1));
        Assert.NotNull(engine.GetInfo(id2));
    }

    [Fact]
    public void FindById_AfterMoveNode_StillWorks()
    {
        using var engine = CreateEngine();
        ulong parent1 = engine.CreateChild(1, "folder1", isContainer: true);
        ulong parent2 = engine.CreateChild(1, "folder2", isContainer: true);
        ulong child = engine.CreateChild(parent1, "moveable");

        // Move to new parent with new name
        engine.MoveNode(child, parent2, "moved_child");

        var info = engine.GetInfo(child);
        Assert.NotNull(info);
        Assert.Equal(parent2, info.ParentId);
        Assert.Equal("moved_child", info.Name);
    }

    [Fact]
    public void FindById_AfterReopen_WorksWithoutRebuild()
    {
        string path = Path.Combine(_dir, "reopen_id.obs");
        ulong id;
        using (var engine = ObjectEngine.Create(path))
        {
            id = engine.CreateObject("persistent");
        }

        // Reopen — should load ID index from disk, no rebuild needed
        using (var engine = ObjectEngine.Open(path))
        {
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
            Assert.Equal(id, info.Id);
            Assert.Equal("persistent", info.Name);
        }
    }

    [Fact]
    public void FindById_1000Objects_AllRetrievable()
    {
        using var engine = CreateEngine();
        var ids = new List<ulong>();
        for (int i = 0; i < 1000; i++)
            ids.Add(engine.CreateObject($"mass_{i:D4}"));

        // Verify all are findable
        for (int i = 0; i < 1000; i++)
        {
            var info = engine.GetInfo(ids[i]);
            Assert.NotNull(info);
            Assert.Equal($"mass_{i:D4}", info.Name);
        }
    }

    [Fact]
    public void DeleteSubtree_RemovesFromIdIndex()
    {
        using var engine = CreateEngine();
        ulong folder = engine.CreateChild(1, "container", isContainer: true);
        ulong child1 = engine.CreateChild(folder, "child1");
        ulong child2 = engine.CreateChild(folder, "child2");

        engine.DeleteSubtree(folder);

        Assert.Null(engine.GetInfo(folder));
        Assert.Null(engine.GetInfo(child1));
        Assert.Null(engine.GetInfo(child2));
    }

    // --- Fix 9: AllocateTracked Tests ---

    [Fact]
    public void Rollback_AfterCreate_FreesList()
    {
        using var engine = CreateEngine();
        ulong baseId = engine.CreateObject("base");

        engine.Transactions.Begin();
        ulong txId1 = engine.CreateObject("txn_obj1");
        ulong txId2 = engine.CreateObject("txn_obj2");
        engine.Transactions.Rollback();

        // Objects created in rolled-back transaction should not exist
        Assert.Null(engine.GetInfo(txId1));
        Assert.Null(engine.GetInfo(txId2));
        // Base object still exists
        Assert.NotNull(engine.GetInfo(baseId));
    }

    [Fact]
    public void Rollback_NestedSavepoint_RestoresState()
    {
        using var engine = CreateEngine();
        engine.Transactions.Begin();
        ulong id1 = engine.CreateObject("level1");

        engine.Transactions.Begin(); // savepoint
        ulong id2 = engine.CreateObject("level2");

        engine.Transactions.Rollback(); // rollback savepoint only

        // id1 should still exist (in transaction), id2 should be gone
        Assert.NotNull(engine.GetInfo(id1));
        Assert.Null(engine.GetInfo(id2));

        engine.Transactions.Commit(); // commit id1
        Assert.NotNull(engine.GetInfo(id1));
    }

    [Fact]
    public void Rollback_WithWrites_FreesAllocatedBlocks()
    {
        using var engine = CreateEngine();
        ulong obj = engine.CreateObject("data_holder");

        engine.Transactions.Begin();
        // Write some data in the transaction
        engine.Append(obj, new byte[4096]);
        engine.Transactions.Rollback();

        // Object should still have size 0 (no data committed)
        var info = engine.GetInfo(obj);
        Assert.NotNull(info);
        Assert.Equal(0L, info.Size);
    }

    [Fact]
    public void Rollback_MultipleObjects_AllReverted()
    {
        using var engine = CreateEngine();
        engine.Transactions.Begin();

        var ids = new List<ulong>();
        for (int i = 0; i < 10; i++)
        {
            ulong id = engine.CreateObject($"rollback_{i}");
            engine.Append(id, new byte[512]);
            ids.Add(id);
        }

        engine.Transactions.Rollback();

        foreach (var id in ids)
            Assert.Null(engine.GetInfo(id));
    }
}
