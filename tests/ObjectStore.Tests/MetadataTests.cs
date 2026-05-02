namespace ObjectStore.Tests;

/// <summary>
/// Tests for per-object metadata (key-value storage).
/// </summary>
public class MetadataTests : IDisposable
{
    private readonly string _path;
    private readonly ObjectEngine _engine;

    public MetadataTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_meta_{Guid.NewGuid():N}.dat");
        _engine = ObjectEngine.Create(_path);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void SetAndGet()
    {
        var id = _engine.CreateObject("meta-obj");
        _engine.SetMetadata(id, "author", "Alice");
        Assert.Equal("Alice", _engine.GetMetadata(id, "author"));
    }

    [Fact]
    public void GetNonexistent_ReturnsNull()
    {
        var id = _engine.CreateObject("meta-obj2");
        Assert.Null(_engine.GetMetadata(id, "missing"));
    }

    [Fact]
    public void OverwriteValue()
    {
        var id = _engine.CreateObject("meta-obj3");
        _engine.SetMetadata(id, "version", "1");
        _engine.SetMetadata(id, "version", "2");
        Assert.Equal("2", _engine.GetMetadata(id, "version"));
    }

    [Fact]
    public void Delete()
    {
        var id = _engine.CreateObject("meta-obj4");
        _engine.SetMetadata(id, "tag", "v1");
        Assert.True(_engine.DeleteMetadata(id, "tag"));
        Assert.Null(_engine.GetMetadata(id, "tag"));
    }

    [Fact]
    public void DeleteNonexistent_ReturnsFalse()
    {
        var id = _engine.CreateObject("meta-obj5");
        Assert.False(_engine.DeleteMetadata(id, "nope"));
    }

    [Fact]
    public void MultipleKeys()
    {
        var id = _engine.CreateObject("meta-obj6");
        _engine.SetMetadata(id, "key1", "val1");
        _engine.SetMetadata(id, "key2", "val2");
        _engine.SetMetadata(id, "key3", "val3");

        Assert.Equal("val1", _engine.GetMetadata(id, "key1"));
        Assert.Equal("val2", _engine.GetMetadata(id, "key2"));
        Assert.Equal("val3", _engine.GetMetadata(id, "key3"));
    }

    [Fact]
    public void PersistsAcrossReopen()
    {
        var id = _engine.CreateObject("meta-persist");
        _engine.SetMetadata(id, "persist-key", "persist-value");
        _engine.Dispose();

        using var engine2 = ObjectEngine.Open(_path);
        Assert.Equal("persist-value", engine2.GetMetadata(id, "persist-key"));
    }

    [Fact]
    public void UnicodeKeysAndValues()
    {
        var id = _engine.CreateObject("meta-unicode");
        _engine.SetMetadata(id, "日本語", "こんにちは");
        Assert.Equal("こんにちは", _engine.GetMetadata(id, "日本語"));
    }
}
