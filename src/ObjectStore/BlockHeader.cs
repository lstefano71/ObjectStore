using System.Buffers.Binary;
using System.IO.Hashing;

namespace ObjectStore;

/// <summary>
/// Block header utilities. Each buddy block starts with a 16-byte header:
///   [0..3]   u32  block_size (total including header)
///   [4..11]  u64  xxHash3 checksum of payload
///   [12]     u8   flags
///   [13..15] u24  payload_length (little-endian)
/// </summary>
public static class BlockHeader
{
    /// <summary>
    /// Writes the block header and payload into the raw buffer.
    /// </summary>
    public static void WriteBlock(Span<byte> raw, ReadOnlySpan<byte> payload, byte flags)
    {
        int blockSize = raw.Length;

        // Write payload at offset 16
        payload.CopyTo(raw[FormatConstants.BlockHeaderSize..]);

        // Compute checksum over the actual payload only
        ulong checksum = XxHash3.HashToUInt64(payload);

        // Write header
        BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[4..], checksum);
        raw[12] = flags;
        // [13..15] store actual payload length as little-endian uint24
        raw[13] = (byte)(payload.Length);
        raw[14] = (byte)(payload.Length >> 8);
        raw[15] = (byte)(payload.Length >> 16);
    }

    /// <summary>
    /// Validates a block's checksum and extracts the payload.
    /// Throws BlockCorruptedException on checksum mismatch.
    /// </summary>
    public static void ValidateAndGetPayload(ReadOnlySpan<byte> raw, out byte[] payload)
    {
        int payloadSize = Validate(raw);
        payload = raw.Slice(FormatConstants.BlockHeaderSize, payloadSize).ToArray();
    }

    /// <summary>
    /// Validates a block's checksum in-place without allocating.
    /// Returns the payload size. The payload starts at offset BlockHeaderSize in the raw buffer.
    /// </summary>
    public static int Validate(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, "Block too small for header.");

        ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(raw[4..]);

        // Read actual payload length from bytes [13..15] (little-endian uint24)
        int payloadSize = raw[13] | (raw[14] << 8) | (raw[15] << 16);
        if (payloadSize < 0 || payloadSize > raw.Length - FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, $"Invalid payload size in header: {payloadSize}");

        var payloadArea = raw.Slice(FormatConstants.BlockHeaderSize, payloadSize);
        ulong actualChecksum = XxHash3.HashToUInt64(payloadArea);

        if (storedChecksum != actualChecksum)
            throw new BlockCorruptedException(0,
                $"Block checksum mismatch: expected 0x{storedChecksum:X16}, got 0x{actualChecksum:X16}");

        return payloadSize;
    }

    /// <summary>
    /// Reads the payload size from a block header without computing the checksum.
    /// Use when checksum validation is intentionally skipped (e.g., ChecksumPolicy.None or MetadataOnly).
    /// </summary>
    public static int ReadPayloadSizeOnly(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, "Block too small for header.");

        int payloadSize = raw[13] | (raw[14] << 8) | (raw[15] << 16);
        if (payloadSize < 0 || payloadSize > raw.Length - FormatConstants.BlockHeaderSize)
            throw new BlockCorruptedException(0, $"Invalid payload size in header: {payloadSize}");

        return payloadSize;
    }

    /// <summary>Reads the flags byte from a block header.</summary>
    public static byte ReadFlags(ReadOnlySpan<byte> raw) => raw[12];
}
