namespace ObjectStore.Tests;

public class FormatConstantsTests
{
    [Fact]
    public void Magic_HasCorrectLength()
    {
        Assert.Equal(FormatConstants.MagicLength, FormatConstants.Magic.Length);
    }

    [Fact]
    public void BlockSizeForOrder_Order0_Is64()
    {
        Assert.Equal(64, FormatConstants.BlockSizeForOrder(0));
    }

    [Fact]
    public void BlockSizeForOrder_Order17_Is8MB()
    {
        Assert.Equal(8 * 1024 * 1024, FormatConstants.BlockSizeForOrder(17));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(1, 128)]
    [InlineData(2, 256)]
    [InlineData(6, 4096)]
    [InlineData(10, 65536)]
    public void BlockSizeForOrder_IsPowerOfTwo(int order, int expected)
    {
        Assert.Equal(expected, FormatConstants.BlockSizeForOrder(order));
    }

    [Fact]
    public void BlockSizeForOrder_InvalidOrder_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatConstants.BlockSizeForOrder(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatConstants.BlockSizeForOrder(18));
    }

    [Fact]
    public void PayloadSizeForOrder_IsBlockSizeMinusHeader()
    {
        for (int i = 0; i < FormatConstants.OrderCount; i++)
        {
            Assert.Equal(
                FormatConstants.BlockSizeForOrder(i) - FormatConstants.BlockHeaderSize,
                FormatConstants.PayloadSizeForOrder(i));
        }
    }

    [Theory]
    [InlineData(1, 0)]     // 1 byte payload fits in order 0 (48 byte payload capacity)
    [InlineData(48, 0)]    // exactly fills order 0 payload
    [InlineData(49, 1)]    // needs order 1 (112 byte payload capacity)
    [InlineData(112, 1)]   // exactly fills order 1
    [InlineData(113, 2)]   // needs order 2
    public void OrderForPayload_ReturnsMinimalOrder(int payloadSize, int expectedOrder)
    {
        Assert.Equal(expectedOrder, FormatConstants.OrderForPayload(payloadSize));
    }

    [Fact]
    public void OrderForPayload_Zero_ReturnsOrder0()
    {
        Assert.Equal(0, FormatConstants.OrderForPayload(0));
    }
}
