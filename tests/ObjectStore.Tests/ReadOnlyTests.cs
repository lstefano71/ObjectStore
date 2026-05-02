namespace ObjectStore.Tests;

/// <summary>
/// Tests for read-only mode and concurrent access.
/// </summary>
public class ReadOnlyAndConcurrencyTests : IDisposable
{
    private readonly string _path;

    public ReadOnlyAndConcurrencyTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_ro_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void ReadOnly_CanReadObjects()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("ro-obj");
            engine.Append(id, "hello"u8);
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.True(roEngine.IsReadOnly);
        Assert.True(roEngine.Exists(id));

        byte[] buf = new byte[5];
        int read = roEngine.ReadAt(id, 0, buf);
        Assert.Equal(5, read);
        Assert.Equal("hello"u8.ToArray(), buf);
    }

    [Fact]
    public void ReadOnly_RejectsCreate()
    {
        using (ObjectEngine.Create(_path)) { }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.CreateObject("fail"));
    }

    [Fact]
    public void ReadOnly_RejectsAppend()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("obj");
            engine.Append(id, "data"u8);
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.Append(id, "more"u8));
    }

    [Fact]
    public void ReadOnly_RejectsDelete()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("obj");
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.DeleteObject(id));
    }

    [Fact]
    public void ReadOnly_RejectsWriteAt()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("obj");
            engine.Append(id, "data"u8);
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.WriteAt(id, 0, "XXXX"u8));
    }

    [Fact]
    public void ReadOnly_RejectsTruncate()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("obj");
            engine.Append(id, "data"u8);
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.Truncate(id, 0));
    }

    [Fact]
    public void ReadOnly_RejectsBeginTransaction()
    {
        using (ObjectEngine.Create(_path)) { }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.BeginTransaction());
    }

    [Fact]
    public void ReadOnly_RejectsMetadataSet()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("obj");
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Throws<ReadOnlyContainerException>(() => roEngine.SetMetadata(id, "k", "v"));
    }

    [Fact]
    public void ReadOnly_CanListObjects()
    {
        using (var engine = ObjectEngine.Create(_path))
        {
            engine.CreateObject("a");
            engine.CreateObject("b");
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        var objects = roEngine.ListObjects().ToList();
        Assert.Equal(2, objects.Count);
    }

    [Fact]
    public void ReadOnly_CanGetMetadata()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("metaobj");
            engine.SetMetadata(id, "key", "value");
        }

        using var roEngine = ObjectEngine.OpenReadOnly(_path);
        Assert.Equal("value", roEngine.GetMetadata(id, "key"));
    }

    [Fact]
    public void MultipleReadOnlyReaders()
    {
        ulong id;
        using (var engine = ObjectEngine.Create(_path))
        {
            id = engine.CreateObject("shared");
            engine.Append(id, "shared-data"u8);
        }

        // Open multiple read-only handles concurrently
        using var ro1 = ObjectEngine.OpenReadOnly(_path);
        using var ro2 = ObjectEngine.OpenReadOnly(_path);

        byte[] buf1 = new byte[11];
        byte[] buf2 = new byte[11];
        ro1.ReadAt(id, 0, buf1);
        ro2.ReadAt(id, 0, buf2);

        Assert.Equal("shared-data"u8.ToArray(), buf1);
        Assert.Equal("shared-data"u8.ToArray(), buf2);
    }
}
