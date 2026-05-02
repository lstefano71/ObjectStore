namespace ObjectStore.Tests;

public class NodeRecordTests
{
    [Fact]
    public void RoundTrip_AllFields()
    {
        var record = new NodeRecord
        {
            Id = 42,
            ParentId = 1,
            NameHash = 0xDEADBEEF,
            Name = "test-object.txt",
            NodeTypeFlags = NodeRecord.FlagHasData,
            Size = 1024,
            ChildCount = 0,
            Created = 1700000000000,
            Modified = 1700000001000,
            ExtentListAddress = 2048,
            MetadataBlockAddress = 4096,
            CompressionCodec = 0,
        };

        byte[] data = record.Serialize();
        var deserialized = NodeRecord.Deserialize(data);

        Assert.Equal(42UL, deserialized.Id);
        Assert.Equal(1UL, deserialized.ParentId);
        Assert.Equal(0xDEADBEEFUL, deserialized.NameHash);
        Assert.Equal("test-object.txt", deserialized.Name);
        Assert.Equal(NodeRecord.FlagHasData, deserialized.NodeTypeFlags);
        Assert.Equal(1024L, deserialized.Size);
        Assert.Equal(0U, deserialized.ChildCount);
        Assert.Equal(1700000000000L, deserialized.Created);
        Assert.Equal(1700000001000L, deserialized.Modified);
        Assert.Equal(2048L, deserialized.ExtentListAddress);
        Assert.Equal(4096L, deserialized.MetadataBlockAddress);
        Assert.Equal((byte)0, deserialized.CompressionCodec);
    }

    [Fact]
    public void RoundTrip_UnicodeNames()
    {
        var record = new NodeRecord
        {
            Id = 1,
            ParentId = 0,
            Name = "日本語ファイル名.txt",
            NameHash = FnvHash.ComputeString("日本語ファイル名.txt"),
            NodeTypeFlags = NodeRecord.FlagHasData | NodeRecord.FlagHasChildren,
        };

        byte[] data = record.Serialize();
        var deserialized = NodeRecord.Deserialize(data);

        Assert.Equal("日本語ファイル名.txt", deserialized.Name);
        Assert.True(deserialized.HasData);
        Assert.True(deserialized.HasChildren);
    }

    [Fact]
    public void RoundTrip_EmptyName()
    {
        var record = new NodeRecord { Id = 5, Name = string.Empty };
        byte[] data = record.Serialize();
        var deserialized = NodeRecord.Deserialize(data);
        Assert.Equal(string.Empty, deserialized.Name);
    }

    [Fact]
    public void Flags_Properties()
    {
        var record = new NodeRecord { NodeTypeFlags = NodeRecord.FlagHasData | NodeRecord.FlagIsDeleted };
        Assert.True(record.HasData);
        Assert.False(record.HasChildren);
        Assert.True(record.IsDeleted);
    }
}
