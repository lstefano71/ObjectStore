namespace ObjectStore.Tests;

public class BlockHeaderTests
{
    [Fact]
    public void WriteBlock_ReadBack_Validates()
    {
        int order = 0; // 64 byte block
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        int payloadCapacity = FormatConstants.PayloadSizeForOrder(order);

        byte[] payload = new byte[payloadCapacity];
        Random.Shared.NextBytes(payload);

        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw, payload, FormatConstants.BlockFlagInUse);

        BlockHeader.ValidateAndGetPayload(raw, out var result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void WriteBlock_PartialPayload_PadsWithZeros()
    {
        int order = 1; // 128 byte block
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        byte[] payload = [1, 2, 3, 4, 5];

        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw, payload, FormatConstants.BlockFlagInUse);

        BlockHeader.ValidateAndGetPayload(raw, out var result);
        // Result is the full payload area
        Assert.Equal(blockSize - FormatConstants.BlockHeaderSize, result.Length);
        Assert.Equal(payload, result[..5]);
    }

    [Fact]
    public void CorruptedPayload_ThrowsBlockCorruptedException()
    {
        int order = 0;
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        byte[] payload = [10, 20, 30];

        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw, payload, FormatConstants.BlockFlagInUse);

        // Corrupt a payload byte
        raw[FormatConstants.BlockHeaderSize + 1] ^= 0xFF;

        Assert.Throws<BlockCorruptedException>(() => BlockHeader.ValidateAndGetPayload(raw, out _));
    }

    [Fact]
    public void ReadFlags_ReturnsCorrectValue()
    {
        int blockSize = FormatConstants.BlockSizeForOrder(0);
        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw, [], FormatConstants.BlockFlagInUse);

        Assert.Equal(FormatConstants.BlockFlagInUse, BlockHeader.ReadFlags(raw));
    }
}
