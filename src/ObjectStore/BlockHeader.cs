using System.Buffers.Binary;
using System.IO.Hashing;

namespace ObjectStore;

/// <summary>
/// Block header utilities. Each buddy block starts with a 16-byte header:
///   [0..3]   u32  block_size (total including header)
///   [4..11]  u64  xxHash3 checksum of payload
///   [12]     u8   flags
///   [13..15] 3B   reserved
/// </summary>
public static class BlockHeader
{
    /// <summary>
    /// Writes the block header and payload into the raw buffer.
    /// </summary>
    public static void WriteBlock(Span<byte> raw, ReadOnlySpan<byte> payload, byte flags)
    {
        int blockSize = raw.Length;
        int payloadCapacity = blockSize - FormatConstants.BlockHeaderSize;

        // Zero the buffer first (ensures reserved bytes and unused payload area are zeroed)
        raw.Clear();

        // Write payload at offset 16
        payload.CopyTo(raw[FormatConstants.BlockHeaderSize..]);

        // Compute checksum over the full payload area (including zero padding)
        var payloadArea = raw.Slice(FormatConstants.BlockHeaderSize, payloadCapacity);
        ulong checksum = XxHash3.HashToUInt64(payloadArea);

        // Write header
        BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[4..], checksum);
        raw[12] = flags;
        // [13..15] reserved, already zero
    }

    /// <summary>
    /// Validates a block's checksum and extracts the payload.
    /// Throws BlockCorruptedException on checksum mismatch.
    /// </summary>
    public static void ValidateAndGetPayload(ReadOnlySpan<byte> raw, out byte[] payload)
    {
        if (raw.Length < FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, "Block too small for header.");

        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(raw[4..]);

        int payloadSize = (int)blockSize - FormatConstants.BlockHeaderSize;
        if (payloadSize < 0 || payloadSize > raw.Length - FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, $"Invalid block size in header: {blockSize}");

        var payloadArea = raw.Slice(FormatConstants.BlockHeaderSize, payloadSize);
        ulong actualChecksum = XxHash3.HashToUInt64(payloadArea);

        if (storedChecksum != actualChecksum)
            throw new BlockCorruptedException(0,
                $"Block checksum mismatch: expected 0x{storedChecksum:X16}, got 0x{actualChecksum:X16}");

        payload = payloadArea.ToArray();
    }

    /// <summary>Reads the flags byte from a block header.</summary>
    public static byte ReadFlags(ReadOnlySpan<byte> raw) => raw[12];
}
