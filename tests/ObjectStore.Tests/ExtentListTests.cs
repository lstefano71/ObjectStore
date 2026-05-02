namespace ObjectStore.Tests;

public class ExtentListTests
{
    [Fact]
    public void RoundTrip_Empty()
    {
        var list = new ExtentList();
        byte[] data = list.Serialize();
        var deserialized = ExtentList.Deserialize(data);
        Assert.Empty(deserialized.Extents);
    }

    [Fact]
    public void RoundTrip_MultipleExtents()
    {
        var list = new ExtentList();
        list.Extents.Add((1024, 0));
        list.Extents.Add((2048, 3));
        list.Extents.Add((65536, 10));

        byte[] data = list.Serialize();
        var deserialized = ExtentList.Deserialize(data);

        Assert.Equal(3, deserialized.Extents.Count);
        Assert.Equal((1024L, 0), deserialized.Extents[0]);
        Assert.Equal((2048L, 3), deserialized.Extents[1]);
        Assert.Equal((65536L, 10), deserialized.Extents[2]);
    }

    [Fact]
    public void TotalCapacity_Computed()
    {
        var list = new ExtentList();
        list.Extents.Add((1024, 0)); // 48 bytes payload
        list.Extents.Add((2048, 1)); // 112 bytes payload

        long expected = FormatConstants.PayloadSizeForOrder(0) + FormatConstants.PayloadSizeForOrder(1);
        Assert.Equal(expected, list.TotalCapacity);
    }

    [Fact]
    public void Serialize_Format_Correct()
    {
        var list = new ExtentList();
        list.Extents.Add((1024, 5));

        byte[] data = list.Serialize();
        // 4 bytes count + 9 bytes per entry
        Assert.Equal(13, data.Length);
    }
}
