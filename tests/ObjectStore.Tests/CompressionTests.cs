namespace ObjectStore.Tests;

public class CompressionTests
{
    [Theory]
    [InlineData(CompressionCodec.Deflate)]
    [InlineData(CompressionCodec.Brotli)]
    public void RoundTrip_SmallData(CompressionCodec codec)
    {
        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = BlockCompression.Compress(original, codec);
        byte[] decompressed = BlockCompression.Decompress(compressed, codec);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(CompressionCodec.Deflate)]
    [InlineData(CompressionCodec.Brotli)]
    public void RoundTrip_LargeData(CompressionCodec codec)
    {
        // Highly compressible data
        byte[] original = new byte[10000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 26 + 'A');

        byte[] compressed = BlockCompression.Compress(original, codec);
        Assert.True(compressed.Length < original.Length); // Actually compresses

        byte[] decompressed = BlockCompression.Decompress(compressed, codec);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void None_Passthrough()
    {
        byte[] original = [1, 2, 3, 4, 5];
        byte[] result = BlockCompression.Compress(original, CompressionCodec.None);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Empty_Data()
    {
        byte[] empty = [];
        byte[] result = BlockCompression.Compress(empty, CompressionCodec.Deflate);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(CompressionCodec.Deflate)]
    [InlineData(CompressionCodec.Brotli)]
    public void RoundTrip_RandomData(CompressionCodec codec)
    {
        byte[] original = new byte[1024];
        Random.Shared.NextBytes(original);

        byte[] compressed = BlockCompression.Compress(original, codec);
        byte[] decompressed = BlockCompression.Decompress(compressed, codec);
        Assert.Equal(original, decompressed);
    }
}
