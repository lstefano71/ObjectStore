namespace ObjectStore.Tests;

public class TransactionTests : IDisposable
{
    private readonly string _path;
    private ObjectEngine? _engine;

    public TransactionTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_txn_{Guid.NewGuid():N}.dat");
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

    [Fact]
    public void AutoCommit_PersistsWithoutExplicitTxn()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("auto");
            engine.Append(id, "data"u8.ToArray());
        }

        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
            byte[] buf = new byte[4];
            engine.ReadAt(id, 0, buf);
            Assert.Equal("data"u8.ToArray(), buf);
        }
    }

    [Fact]
    public void Commit_Persists()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            engine.BeginTransaction();
            id = engine.CreateObject("txn-persist");
            engine.Append(id, "hello"u8.ToArray());
            engine.CommitTransaction();
        }

        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
        }
    }

    [Fact]
    public void Rollback_RevertsCreate()
    {
        var engine = CreateEngine();
        ulong id;

        engine.BeginTransaction();
        id = engine.CreateObject("will-rollback");
        engine.Append(id, "data"u8.ToArray());
        engine.RollbackTransaction();

        Assert.False(engine.Exists(id));
    }

    [Fact]
    public void Rollback_RevertsDelete()
    {
        var engine = CreateEngine();

        // Create object (auto-committed)
        ulong id = engine.CreateObject("keep-me");
        engine.Append(id, "data"u8.ToArray());

        // Delete inside transaction, then rollback
        engine.BeginTransaction();
        engine.DeleteObject(id);
        engine.RollbackTransaction();

        Assert.True(engine.Exists(id));
        byte[] buf = new byte[4];
        engine.ReadAt(id, 0, buf);
        Assert.Equal("data"u8.ToArray(), buf);
    }

    [Fact]
    public void NestedSavepoint_RollbackInner()
    {
        var engine = CreateEngine();

        engine.BeginTransaction();
        ulong id1 = engine.CreateObject("outer");

        // Nested savepoint
        engine.BeginTransaction();
        ulong id2 = engine.CreateObject("inner");
        engine.RollbackTransaction(); // rollback inner

        engine.CommitTransaction(); // commit outer

        Assert.True(engine.Exists(id1));
        Assert.False(engine.Exists(id2));
    }

    [Fact]
    public void NestedSavepoint_CommitInner()
    {
        var engine = CreateEngine();

        engine.BeginTransaction();
        ulong id1 = engine.CreateObject("outer");

        engine.BeginTransaction();
        ulong id2 = engine.CreateObject("inner");
        engine.CommitTransaction(); // commit inner (merges into outer)

        engine.CommitTransaction(); // commit outer

        Assert.True(engine.Exists(id1));
        Assert.True(engine.Exists(id2));
    }

    [Fact]
    public void RollbackOuter_RevertsAll()
    {
        var engine = CreateEngine();

        engine.BeginTransaction();
        ulong id1 = engine.CreateObject("outer");

        engine.BeginTransaction();
        ulong id2 = engine.CreateObject("inner");
        engine.CommitTransaction(); // commit inner (merges into outer)

        engine.RollbackTransaction(); // rollback entire outer

        Assert.False(engine.Exists(id1));
        Assert.False(engine.Exists(id2));
    }

    [Fact]
    public void Commit_WithoutBegin_Throws()
    {
        var engine = CreateEngine();
        Assert.Throws<InvalidOperationException>(() => engine.CommitTransaction());
    }

    [Fact]
    public void Rollback_WithoutBegin_Throws()
    {
        var engine = CreateEngine();
        Assert.Throws<InvalidOperationException>(() => engine.RollbackTransaction());
    }

    [Fact]
    public void Transaction_MultipleOperations()
    {
        var engine = CreateEngine();

        // Pre-existing objects
        ulong preId = engine.CreateObject("pre-existing");
        engine.Append(preId, "original"u8.ToArray());

        engine.BeginTransaction();

        ulong newId = engine.CreateObject("new-obj");
        engine.Append(newId, "new-data"u8.ToArray());
        engine.Append(preId, " more"u8.ToArray()); // append to existing

        engine.CommitTransaction();

        Assert.True(engine.Exists(newId));
        byte[] buf = new byte[13];
        engine.ReadAt(preId, 0, buf);
        Assert.Equal("original more"u8.ToArray(), buf);
    }

    [Fact]
    public void Rollback_PreservesPreExistingData()
    {
        var engine = CreateEngine();

        ulong preId = engine.CreateObject("pre");
        engine.Append(preId, "keep"u8.ToArray());

        engine.BeginTransaction();
        engine.CreateObject("discard");
        engine.RollbackTransaction();

        // Pre-existing object should still be there
        Assert.True(engine.Exists(preId));
        byte[] buf = new byte[4];
        engine.ReadAt(preId, 0, buf);
        Assert.Equal("keep"u8.ToArray(), buf);
    }
}
