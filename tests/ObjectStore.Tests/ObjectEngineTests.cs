namespace ObjectStore.Tests;

public class ObjectEngineTests : IDisposable
{
    private readonly string _path;
    private ObjectEngine? _engine;

    public ObjectEngineTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_test_{Guid.NewGuid():N}.dat");
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
    public void CreateAndOpen_RoundTrip()
    {
        // Create a store and add an object
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("hello");
            Assert.True(engine.Exists(id));
        }

        // Reopen and verify the object persists
        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
            Assert.Equal("hello", info.Name);
        }
    }

    [Fact]
    public void CreateObject_AssignsUniqueIds()
    {
        var engine = CreateEngine();
        ulong id1 = engine.CreateObject("obj1");
        ulong id2 = engine.CreateObject("obj2");
        ulong id3 = engine.CreateObject("obj3");

        Assert.NotEqual(id1, id2);
        Assert.NotEqual(id2, id3);
        Assert.True(id1 < id2 && id2 < id3);
    }

    [Fact]
    public void DeleteObject_RemovesFromStore()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("to-delete");
        Assert.True(engine.Exists(id));

        bool deleted = engine.DeleteObject(id);
        Assert.True(deleted);
        Assert.False(engine.Exists(id));
    }

    [Fact]
    public void DeleteObject_NonExistent_ReturnsFalse()
    {
        var engine = CreateEngine();
        bool deleted = engine.DeleteObject(999);
        Assert.False(deleted);
    }

    [Fact]
    public void Append_And_ReadAt()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("data-obj");

        byte[] data = "Hello, World!"u8.ToArray();
        engine.Append(id, data);

        var info = engine.GetInfo(id);
        Assert.Equal(data.Length, info!.Size);

        byte[] buffer = new byte[data.Length];
        int read = engine.ReadAt(id, 0, buffer);
        Assert.Equal(data.Length, read);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void Append_MultipleTimes()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("multi-append");

        byte[] part1 = "Hello, "u8.ToArray();
        byte[] part2 = "World!"u8.ToArray();
        engine.Append(id, part1);
        engine.Append(id, part2);

        var info = engine.GetInfo(id);
        Assert.Equal(part1.Length + part2.Length, info!.Size);

        byte[] buffer = new byte[part1.Length + part2.Length];
        int read = engine.ReadAt(id, 0, buffer);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("Hello, World!", System.Text.Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public void ReadAt_PartialRead()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("partial");
        engine.Append(id, "ABCDEFGHIJ"u8.ToArray());

        byte[] buffer = new byte[3];
        int read = engine.ReadAt(id, 5, buffer);
        Assert.Equal(3, read);
        Assert.Equal("FGH", System.Text.Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public void ReadAt_BeyondEnd_ReturnsZero()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("short");
        engine.Append(id, "AB"u8.ToArray());

        byte[] buffer = new byte[10];
        int read = engine.ReadAt(id, 100, buffer);
        Assert.Equal(0, read);
    }

    [Fact]
    public void WriteAt_Overwrite()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("overwrite");
        engine.Append(id, "Hello, World!"u8.ToArray());

        engine.WriteAt(id, 7, "EARTH!"u8.ToArray());

        byte[] buffer = new byte[13];
        engine.ReadAt(id, 0, buffer);
        Assert.Equal("Hello, EARTH!", System.Text.Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public void WriteAt_BeyondEnd_Throws()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("short");
        engine.Append(id, "AB"u8.ToArray());

        Assert.Throws<ArgumentException>(() =>
            engine.WriteAt(id, 0, "TOOLONG"u8.ToArray()));
    }

    [Fact]
    public void Truncate_ReducesSize()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("truncate");
        engine.Append(id, "Hello, World! Extra data here."u8.ToArray());

        engine.Truncate(id, 5);

        var info = engine.GetInfo(id);
        Assert.Equal(5L, info!.Size);
    }

    [Fact]
    public void ListObjects_ReturnsAll()
    {
        var engine = CreateEngine();
        engine.CreateObject("obj1");
        engine.CreateObject("obj2");
        engine.CreateObject("obj3");

        var objects = engine.ListObjects().ToList();
        Assert.Equal(3, objects.Count);
    }

    [Fact]
    public void LargeData_MultiExtent()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("large");

        // Write 1KB of data (will go into one extent with order 4 or higher)
        byte[] data = new byte[1024];
        Random.Shared.NextBytes(data);
        engine.Append(id, data);

        byte[] readBack = new byte[1024];
        int bytesRead = engine.ReadAt(id, 0, readBack);
        Assert.Equal(1024, bytesRead);
        Assert.Equal(data, readBack);
    }

    [Fact]
    public void Persistence_AfterReopen()
    {
        byte[] data = "Persistent data test!"u8.ToArray();
        ulong id;

        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("persist");
            engine.Append(id, data);
        }

        using (var engine = ObjectEngine.Open(_path))
        {
            Assert.True(engine.Exists(id));
            byte[] buffer = new byte[data.Length];
            int read = engine.ReadAt(id, 0, buffer);
            Assert.Equal(data.Length, read);
            Assert.Equal(data, buffer);
        }
    }

    [Fact]
    public void OpenOrCreate_NewFile()
    {
        using var engine = ObjectEngine.OpenOrCreate(_path);
        ulong id = engine.CreateObject("new");
        Assert.True(engine.Exists(id));
    }

    [Fact]
    public void OpenOrCreate_ExistingFile()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("existing");
        }

        _engine = ObjectEngine.OpenOrCreate(_path);
        Assert.True(_engine.Exists(id));
    }
}
