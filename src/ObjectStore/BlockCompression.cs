using System.IO.Compression;

namespace ObjectStore;

/// <summary>
/// Codec identifiers for block-level compression.
/// </summary>
public enum CompressionCodec : byte
{
    None = 0,
    Deflate = 1,
    Brotli = 2,
}

/// <summary>
/// Handles per-block compression and decompression.
/// </summary>
public static class BlockCompression
{
    /// <summary>Compresses data with the specified codec. Returns compressed bytes.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionCodec codec)
    {
        if (codec == CompressionCodec.None || data.Length == 0)
            return data.ToArray();

        using var output = new MemoryStream();

        // Write original size header (4 bytes) so we know decompressed size
        Span<byte> sizeHeader = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
        output.Write(sizeHeader);

        using (Stream compressor = codec switch
        {
            CompressionCodec.Deflate => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
            CompressionCodec.Brotli => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
            _ => throw new ArgumentException($"Unknown codec: {codec}")
        })
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses data with the specified codec.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data, CompressionCodec codec)
    {
        if (codec == CompressionCodec.None || data.Length == 0)
            return data.ToArray();

        // Read original size from header
        int originalSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
        var compressed = data[4..];

        using var input = new MemoryStream(compressed.ToArray());
        using Stream decompressor = codec switch
        {
            CompressionCodec.Deflate => new DeflateStream(input, CompressionMode.Decompress),
            CompressionCodec.Brotli => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentException($"Unknown codec: {codec}")
        };

        byte[] result = new byte[originalSize];
        int totalRead = 0;
        while (totalRead < originalSize)
        {
            int read = decompressor.Read(result, totalRead, originalSize - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return result;
    }
}
