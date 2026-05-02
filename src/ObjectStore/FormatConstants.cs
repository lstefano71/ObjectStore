namespace ObjectStore;

/// <summary>
/// On-disk format constants for the ObjectStore container file.
/// </summary>
public static class FormatConstants
{
    /// <summary>Magic bytes identifying an ObjectStore container: "OBJSTORE\0"</summary>
    public static ReadOnlySpan<byte> Magic => "OBJSTORE\0"u8;

    /// <summary>Length of the magic bytes (9 bytes).</summary>
    public const int MagicLength = 9;

    /// <summary>Format version major number.</summary>
    public const ushort VersionMajor = 1;

    /// <summary>Format version minor number.</summary>
    public const ushort VersionMinor = 0;

    /// <summary>Size of each superblock in bytes.</summary>
    public const int SuperblockSize = 512;

    /// <summary>Offset of Superblock A from the start of the file.</summary>
    public const long SuperblockAOffset = 0;

    /// <summary>Offset of Superblock B from the start of the file.</summary>
    public const long SuperblockBOffset = SuperblockSize;

    /// <summary>Offset where the data region begins (after both superblocks).</summary>
    public const long DataRegionOffset = 1024;

    /// <summary>Minimum block size (order 0) = 64 bytes.</summary>
    public const int MinBlockSize = 64;

    /// <summary>Maximum block size (order 17) = 8 MB.</summary>
    public const int MaxBlockSize = 8 * 1024 * 1024;

    /// <summary>Number of buddy allocator orders (0..17).</summary>
    public const int OrderCount = 18;

    /// <summary>Size of the block header prepended to every buddy block.</summary>
    public const int BlockHeaderSize = 16;

    // Block header layout (16 bytes):
    //   [0..3]   u32 block_size (total including header)
    //   [4..11]  u64 xxHash3 checksum of payload (bytes after header)
    //   [12]     u8  flags
    //   [13..15] 3 bytes reserved

    /// <summary>Block header flags: block is in use.</summary>
    public const byte BlockFlagInUse = 0x01;

    /// <summary>Block header flags: block is free (in free list).</summary>
    public const byte BlockFlagFree = 0x02;

    /// <summary>Returns the total block size (including header) for a given order.</summary>
    public static int BlockSizeForOrder(int order)
    {
        if (order < 0 || order >= OrderCount)
            throw new ArgumentOutOfRangeException(nameof(order));
        return MinBlockSize << order;
    }

    /// <summary>Returns the usable payload size for a given order (block size minus header).</summary>
    public static int PayloadSizeForOrder(int order) => BlockSizeForOrder(order) - BlockHeaderSize;

    /// <summary>Computes the minimum order required to hold a payload of the given size.</summary>
    public static int OrderForPayload(int payloadSize)
    {
        if (payloadSize <= 0)
            return 0;

        int needed = payloadSize + BlockHeaderSize;
        int order = 0;
        int blockSize = MinBlockSize;
        while (blockSize < needed && order < OrderCount - 1)
        {
            order++;
            blockSize <<= 1;
        }
        if (blockSize < needed)
            throw new ArgumentException($"Payload size {payloadSize} exceeds maximum block capacity.");
        return order;
    }
}
