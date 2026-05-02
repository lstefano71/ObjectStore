using System.Buffers.Binary;
using System.IO.Hashing;

namespace ObjectStore;

/// <summary>
/// Represents the on-disk superblock structure (512 bytes fixed).
/// </summary>
public struct Superblock
{
    /// <summary>Format version major.</summary>
    public ushort VersionMajor;

    /// <summary>Format version minor.</summary>
    public ushort VersionMinor;

    /// <summary>Monotonically-increasing generation counter. Higher = newer.</summary>
    public ulong Generation;

    /// <summary>Block address of the B-tree root node.</summary>
    public ulong BTreeRootAddress;

    /// <summary>Block address of the buddy allocator state.</summary>
    public ulong BuddyRootAddress;

    /// <summary>Total container file size in bytes at the time of this superblock write.</summary>
    public ulong ContainerSize;

    /// <summary>Superblock flags (bit 0 = encryption enabled, bit 1 = dirty-open).</summary>
    public uint Flags;

    /// <summary>Next node/object ID to assign (monotonic counter).</summary>
    public ulong NextNodeId;

    /// <summary>Root node ID (always 1 after initial creation).</summary>
    public ulong RootNodeId;

    /// <summary>Block address of the secondary ID-index B-tree root.</summary>
    public ulong IdIndexRootAddress;

    // Flag bit constants
    public const uint FlagEncryptionEnabled = 0x01;
    public const uint FlagDirtyOpen = 0x02;

    /// <summary>
    /// Serializes this superblock into exactly 512 bytes.
    /// The last 8 bytes are an xxHash3 checksum of the preceding 504 bytes.
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < FormatConstants.SuperblockSize)
            throw new ArgumentException("Buffer too small for superblock.");

        buffer.Clear();

        // Magic bytes at offset 0
        FormatConstants.Magic.CopyTo(buffer);
        int offset = FormatConstants.MagicLength; // 9

        // Version
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[offset..], VersionMajor);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[offset..], VersionMinor);
        offset += 2;

        // Padding to align to 8 bytes (offset is 13, pad to 16)
        offset = 16;

        // Generation
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], Generation);
        offset += 8;

        // B-tree root address
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], BTreeRootAddress);
        offset += 8;

        // Buddy root address
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], BuddyRootAddress);
        offset += 8;

        // Container size
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], ContainerSize);
        offset += 8;

        // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], Flags);
        offset += 4;

        // Padding to 8-byte boundary
        offset = 60;

        // NextNodeId
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], NextNodeId);
        offset += 8;

        // RootNodeId
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], RootNodeId);
        offset += 8;

        // IdIndexRootAddress
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], IdIndexRootAddress);
        offset += 8;

        // Checksum at the last 8 bytes (offset 504..512)
        var payload = buffer[..504];
        ulong checksum = XxHash3.HashToUInt64(payload);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[504..], checksum);
    }

    /// <summary>
    /// Deserializes a superblock from exactly 512 bytes.
    /// Returns false if magic bytes don't match or checksum is invalid.
    /// </summary>
    public static bool TryReadFrom(ReadOnlySpan<byte> buffer, out Superblock sb)
    {
        sb = default;

        if (buffer.Length < FormatConstants.SuperblockSize)
            return false;

        // Validate magic
        if (!buffer[..FormatConstants.MagicLength].SequenceEqual(FormatConstants.Magic))
            return false;

        // Validate checksum
        var payload = buffer[..504];
        ulong expectedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer[504..]);
        ulong actualChecksum = XxHash3.HashToUInt64(payload);
        if (expectedChecksum != actualChecksum)
            return false;

        int offset = FormatConstants.MagicLength; // 9

        sb.VersionMajor = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
        offset += 2;
        sb.VersionMinor = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
        offset += 2;

        offset = 16; // aligned

        sb.Generation = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.BTreeRootAddress = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.BuddyRootAddress = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.ContainerSize = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.Flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        offset += 4;

        offset = 60;

        sb.NextNodeId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.RootNodeId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
        offset += 8;

        sb.IdIndexRootAddress = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);

        return true;
    }

    /// <summary>Validates that the version is compatible with this library.</summary>
    public readonly bool IsVersionCompatible =>
        VersionMajor == FormatConstants.VersionMajor;
}
