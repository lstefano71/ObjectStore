namespace ObjectStore.Tests;

public class BTreeKeyTests
{
    [Fact]
    public void CompareTo_OrdersByParentIdFirst()
    {
        var k1 = new BTreeKey(1, 100);
        var k2 = new BTreeKey(2, 50);
        Assert.True(k1 < k2);
    }

    [Fact]
    public void CompareTo_SameParent_OrdersByNameHash()
    {
        var k1 = new BTreeKey(1, 50);
        var k2 = new BTreeKey(1, 100);
        Assert.True(k1 < k2);
    }

    [Fact]
    public void CompareTo_Equal_ReturnsZero()
    {
        var k1 = new BTreeKey(5, 10);
        var k2 = new BTreeKey(5, 10);
        Assert.Equal(0, k1.CompareTo(k2));
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var key = new BTreeKey(123456789, 987654321);
        Span<byte> buf = stackalloc byte[BTreeKey.Size];
        key.WriteTo(buf);

        var loaded = BTreeKey.ReadFrom(buf);
        Assert.Equal(key, loaded);
    }
}
