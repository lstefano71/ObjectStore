using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// Power-of-two buddy allocator managing block allocation within the container file.
/// 18 orders (0..17) corresponding to block sizes 64B..8MB.
/// Free lists are intrusive linked lists stored in the free blocks themselves.
/// </summary>
public sealed class BuddyAllocator
{
    // Each free list stores the file address of the first free block at that order.
    // A value of 0 means the list is empty.
    // Inside each free block, the first 8 bytes of the payload area store the "next" pointer.
    private readonly long[] _freeListHeads = new long[FormatConstants.OrderCount];
    private readonly ContainerFile _file;
    private long _dataRegionEnd;

    /// <summary>Total number of blocks currently tracked as free.</summary>
    public int FreeBlockCount { get; private set; }

    public BuddyAllocator(ContainerFile file, long dataRegionEnd)
    {
        _file = file;
        _dataRegionEnd = dataRegionEnd;
    }

    /// <summary>Gets the current data region end (one past the last allocated/managed byte).</summary>
    public long DataRegionEnd => _dataRegionEnd;

    /// <summary>
    /// Allocates a block of the specified order. Returns the file address of the allocated block.
    /// Splits larger blocks if no block of the exact order is available.
    /// </summary>
    public long Allocate(int order)
    {
        if (order < 0 || order >= FormatConstants.OrderCount)
            throw new ArgumentOutOfRangeException(nameof(order));

        // Find the smallest available order >= requested
        for (int i = order; i < FormatConstants.OrderCount; i++)
        {
            if (_freeListHeads[i] != 0)
            {
                long address = PopFreeBlock(i);

                // Split down to the requested order
                while (i > order)
                {
                    i--;
                    long buddyAddress = address + FormatConstants.BlockSizeForOrder(i);
                    PushFreeBlock(i, buddyAddress);
                }

                return address;
            }
        }

        // No free block available — grow the file
        return GrowAndAllocate(order);
    }

    /// <summary>
    /// Frees a block at the given address and order. Coalesces with buddy if possible.
    /// </summary>
    public void Free(long address, int order)
    {
        if (order < 0 || order >= FormatConstants.OrderCount)
            throw new ArgumentOutOfRangeException(nameof(order));

        while (order < FormatConstants.OrderCount - 1)
        {
            long buddyAddress = GetBuddyAddress(address, order);

            // Check if buddy is free and can be coalesced
            if (!TryRemoveFromFreeList(order, buddyAddress))
                break;

            // Coalesce: merge with buddy into parent block
            address = Math.Min(address, buddyAddress);
            order++;
        }

        PushFreeBlock(order, address);
    }

    /// <summary>
    /// Returns the buddy's address for a block at the given address and order.
    /// </summary>
    public static long GetBuddyAddress(long address, int order)
    {
        long relativeAddress = address - FormatConstants.DataRegionOffset;
        long blockSize = FormatConstants.BlockSizeForOrder(order);
        long buddyRelative = relativeAddress ^ blockSize;
        return buddyRelative + FormatConstants.DataRegionOffset;
    }

    /// <summary>Serializes the allocator state into a byte array for persistence.</summary>
    public byte[] Serialize()
    {
        // Format: [dataRegionEnd: u64][freeBlockCount: i32][18 × (headAddr: u64)]
        // Total: 8 + 4 + 18*8 = 156 bytes fixed header
        // Then for each non-empty list, walk and store all addresses.
        // Simpler: store just the heads + data region end. The linked lists are in the file.
        int size = 8 + 4 + FormatConstants.OrderCount * 8;
        byte[] data = new byte[size];
        var span = data.AsSpan();

        BinaryPrimitives.WriteInt64LittleEndian(span, _dataRegionEnd);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], FreeBlockCount);
        for (int i = 0; i < FormatConstants.OrderCount; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[(12 + i * 8)..], _freeListHeads[i]);
        }

        return data;
    }

    /// <summary>Deserializes the allocator state from a byte array.</summary>
    public void Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 + FormatConstants.OrderCount * 8)
            throw new ArgumentException("Buddy allocator state data too short.");

        _dataRegionEnd = BinaryPrimitives.ReadInt64LittleEndian(data);
        FreeBlockCount = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        for (int i = 0; i < FormatConstants.OrderCount; i++)
        {
            _freeListHeads[i] = BinaryPrimitives.ReadInt64LittleEndian(data[(12 + i * 8)..]);
        }
    }

    /// <summary>Creates a snapshot of the free list heads for use in transaction state.</summary>
    public long[] SnapshotFreeListHeads()
    {
        return (long[])_freeListHeads.Clone();
    }

    /// <summary>Restores free list heads from a snapshot (for rollback).</summary>
    public void RestoreFromSnapshot(long[] snapshot, long dataRegionEnd, int freeBlockCount)
    {
        Array.Copy(snapshot, _freeListHeads, FormatConstants.OrderCount);
        _dataRegionEnd = dataRegionEnd;
        FreeBlockCount = freeBlockCount;
    }

    /// <summary>Gets the address of the free list head for a given order (0 if empty).</summary>
    public long GetFreeListHead(int order) => _freeListHeads[order];

    private long PopFreeBlock(int order)
    {
        long address = _freeListHeads[order];
        if (address == 0)
            throw new InvalidOperationException($"Free list for order {order} is empty.");

        // Read the "next" pointer from the block's payload area
        Span<byte> nextBuf = stackalloc byte[8];
        _file.ReadRaw(address + FormatConstants.BlockHeaderSize, nextBuf);
        long next = BinaryPrimitives.ReadInt64LittleEndian(nextBuf);

        _freeListHeads[order] = next;
        FreeBlockCount--;
        return address;
    }

    private void PushFreeBlock(int order, long address)
    {
        // Write the current head as the "next" pointer in this block's payload area
        Span<byte> nextBuf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(nextBuf, _freeListHeads[order]);
        _file.WriteRaw(address + FormatConstants.BlockHeaderSize, nextBuf);

        // Mark block as free in header
        Span<byte> flagBuf = stackalloc byte[1];
        flagBuf[0] = FormatConstants.BlockFlagFree;
        _file.WriteRaw(address + 12, flagBuf); // flags at offset 12 in block header

        _freeListHeads[order] = address;
        FreeBlockCount++;
    }

    private bool TryRemoveFromFreeList(int order, long targetAddress)
    {
        if (_freeListHeads[order] == 0)
            return false;

        // Check if head is the target
        if (_freeListHeads[order] == targetAddress)
        {
            PopFreeBlock(order);
            FreeBlockCount++; // PopFreeBlock decrements, but we don't want net change here
            _freeListHeads[order] = ReadNextPointer(targetAddress);
            FreeBlockCount--;
            return true;
        }

        // Walk the list to find and remove the target
        long prev = _freeListHeads[order];
        long current = ReadNextPointer(prev);

        while (current != 0)
        {
            if (current == targetAddress)
            {
                long next = ReadNextPointer(current);
                WriteNextPointer(prev, next);
                FreeBlockCount--;
                return true;
            }
            prev = current;
            current = ReadNextPointer(current);
        }

        return false;
    }

    private long ReadNextPointer(long blockAddress)
    {
        Span<byte> buf = stackalloc byte[8];
        _file.ReadRaw(blockAddress + FormatConstants.BlockHeaderSize, buf);
        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    private void WriteNextPointer(long blockAddress, long next)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, next);
        _file.WriteRaw(blockAddress + FormatConstants.BlockHeaderSize, buf);
    }

    private long GrowAndAllocate(int order)
    {
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        long address = _dataRegionEnd;

        // Align to block size boundary
        long alignment = blockSize;
        long misalign = (address - FormatConstants.DataRegionOffset) % alignment;
        if (misalign != 0)
            address += alignment - misalign;

        long newEnd = address + blockSize;
        _file.EnsureSize(newEnd);
        _dataRegionEnd = newEnd;

        return address;
    }

    /// <summary>Resets all free lists to empty (used during recovery rebuild).</summary>
    public void Reset()
    {
        Array.Clear(_freeListHeads);
        FreeBlockCount = 0;
        _dataRegionEnd = FormatConstants.DataRegionOffset;
    }

    /// <summary>
    /// Rebuilds the allocator from a set of reachable blocks.
    /// Allocates space up to maxAllocated, then frees blocks not in the reachable set.
    /// </summary>
    public void RebuildFromReachable(long dataStart, long maxAllocated, HashSet<long> reachableBlocks, ContainerFile file)
    {
        _dataRegionEnd = maxAllocated;

        // Walk through the data region, identify blocks from the reachable set
        // and free everything else. We identify block boundaries by reading headers.
        Span<byte> header = stackalloc byte[4];
        long pos = dataStart;
        while (pos < maxAllocated)
        {
            // Try to read block size from header
            file.ReadRaw(pos, header);
            int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);

            if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            {
                // Skip forward by minimum block size if we can't read a valid header
                pos += FormatConstants.MinBlockSize;
                continue;
            }

            int order = 0;
            int sz = FormatConstants.MinBlockSize;
            while (sz < blockSize && order < FormatConstants.OrderCount - 1) { order++; sz <<= 1; }

            if (!reachableBlocks.Contains(pos))
            {
                // This block is not reachable — free it (without coalescing for simplicity)
                PushFreeBlock(order, pos);
            }

            pos += blockSize;
        }
    }
}
