namespace ObjectStore.Tests;

/// <summary>
/// Tests for the public ObjectStoreDatabase API and ObjectInfo.
/// </summary>
public class ObjectStoreDatabaseTests : IDisposable
{
    private readonly string _path;
    private ObjectStoreDatabase _db;

    public ObjectStoreDatabaseTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_db_{Guid.NewGuid():N}.dat");
        _db = ObjectStoreDatabase.Create(_path);
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void CreateAndOpen()
    {
        ulong id = _db.CreateObject("hello");
        _db.Append(id, "world"u8);
        _db.Dispose();

        _db = ObjectStoreDatabase.Open(_path);
        var info = _db.GetInfo(id);
        Assert.NotNull(info);
        Assert.Equal("hello", info.Name);
        Assert.Equal(5, info.Size);
    }

    [Fact]
    public void OpenOrCreate_CreatesNew()
    {
        _db.Dispose();
        File.Delete(_path);
        _db = ObjectStoreDatabase.OpenOrCreate(_path);
        var id = _db.CreateObject("test");
        Assert.True(_db.Exists(id));
    }

    [Fact]
    public void OpenOrCreate_OpensExisting()
    {
        var id = _db.CreateObject("existing");
        _db.Dispose();
        _db = ObjectStoreDatabase.OpenOrCreate(_path);
        Assert.True(_db.Exists(id));
    }

    [Fact]
    public void ListObjects_ReturnsAll()
    {
        _db.CreateObject("a");
        _db.CreateObject("b");
        _db.CreateObject("c");

        var objects = _db.ListObjects().ToList();
        Assert.Equal(3, objects.Count);
        Assert.Contains(objects, o => o.Name == "a");
        Assert.Contains(objects, o => o.Name == "b");
        Assert.Contains(objects, o => o.Name == "c");
    }

    [Fact]
    public void GetInfo_ReturnsObjectInfo()
    {
        var id = _db.CreateObject("infotest");
        _db.Append(id, new byte[100]);

        var info = _db.GetInfo(id);
        Assert.NotNull(info);
        Assert.Equal(id, info.Id);
        Assert.Equal("infotest", info.Name);
        Assert.Equal(100, info.Size);
        Assert.True(info.Created <= DateTimeOffset.UtcNow);
        Assert.True(info.Modified <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void GetInfo_NotFound_ReturnsNull()
    {
        Assert.Null(_db.GetInfo(9999));
    }

    [Fact]
    public void DeleteObject_RemovesFromList()
    {
        var id = _db.CreateObject("del");
        Assert.True(_db.DeleteObject(id));
        Assert.False(_db.Exists(id));
        Assert.Empty(_db.ListObjects());
    }

    [Fact]
    public void ReadWriteAppendTruncate()
    {
        var id = _db.CreateObject("rw");
        _db.Append(id, "hello"u8);

        byte[] buf = new byte[5];
        int read = _db.ReadAt(id, 0, buf);
        Assert.Equal(5, read);
        Assert.Equal("hello"u8.ToArray(), buf);

        _db.WriteAt(id, 0, "HELLO"u8);
        read = _db.ReadAt(id, 0, buf);
        Assert.Equal("HELLO"u8.ToArray(), buf);

        _db.Truncate(id, 3);
        var info = _db.GetInfo(id);
        Assert.Equal(3, info!.Size);
    }

    [Fact]
    public void Transaction_CommitAndRollback()
    {
        _db.BeginTransaction();
        var id = _db.CreateObject("txn");
        _db.RollbackTransaction();
        Assert.False(_db.Exists(id));

        _db.BeginTransaction();
        var id2 = _db.CreateObject("txn2");
        _db.CommitTransaction();
        Assert.True(_db.Exists(id2));
    }

    [Fact]
    public void GetStats()
    {
        _db.CreateObject("s1");
        var id2 = _db.CreateObject("s2");
        _db.Append(id2, new byte[1000]);

        var stats = _db.GetStats();
        Assert.Equal(2, stats.ObjectCount);
        Assert.Equal(1000, stats.TotalDataSize);
        Assert.True(stats.ContainerFileSize > 0);
    }

    [Fact]
    public void Disposed_ThrowsObjectDisposedException()
    {
        _db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _db.CreateObject("x"));
        _db = ObjectStoreDatabase.Create(Path.Combine(Path.GetTempPath(), $"objstore_db2_{Guid.NewGuid():N}.dat"));
    }
}
