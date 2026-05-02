using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// Power-of-two buddy allocator managing block allocation within the container file.
/// 18 orders (0..17) corresponding to block sizes 64B..8MB.
/// Free lists are maintained entirely in memory and persisted as a complete snapshot on commit.
/// </summary>
public sealed class BuddyAllocator
{
    // In-memory free lists: one list of free block addresses per order
    private readonly List<long>[] _freeLists = new List<long>[FormatConstants.OrderCount];
    private readonly ContainerFile _file;
    private long _dataRegionEnd;

    /// <summary>Total number of blocks currently tracked as free.</summary>
    public int FreeBlockCount { get; private set; }

    public BuddyAllocator(ContainerFile file, long dataRegionEnd)
    {
        _file = file;
        _dataRegionEnd = dataRegionEnd;
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            _freeLists[i] = new List<long>();
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
            if (_freeLists[i].Count > 0)
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
        // Format: [dataRegionEnd: i64][freeBlockCount: i32][orderCount: i32]
        // Then for each order: [count: i32][addresses: count × i64]
        int size = 8 + 4 + 4; // header
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            size += 4 + _freeLists[i].Count * 8;

        byte[] data = new byte[size];
        var span = data.AsSpan();
        int offset = 0;

        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], _dataRegionEnd); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], FreeBlockCount); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], FormatConstants.OrderCount); offset += 4;

        for (int i = 0; i < FormatConstants.OrderCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], _freeLists[i].Count); offset += 4;
            foreach (long addr in _freeLists[i])
            {
                BinaryPrimitives.WriteInt64LittleEndian(span[offset..], addr); offset += 8;
            }
        }

        return data;
    }

    /// <summary>Returns the serialized size without actually serializing.</summary>
    public int SerializedSize
    {
        get
        {
            int size = 8 + 4 + 4;
            for (int i = 0; i < FormatConstants.OrderCount; i++)
                size += 4 + _freeLists[i].Count * 8;
            return size;
        }
    }

    /// <summary>Deserializes the allocator state from a byte array.</summary>
    public void Deserialize(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        _dataRegionEnd = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        FreeBlockCount = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]); offset += 4;
        int orderCount = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]); offset += 4;

        for (int i = 0; i < Math.Min(orderCount, FormatConstants.OrderCount); i++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]); offset += 4;
            _freeLists[i].Clear();
            for (int j = 0; j < count; j++)
            {
                long addr = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
                _freeLists[i].Add(addr);
            }
        }
    }

    /// <summary>Creates a snapshot of the free lists for use in transaction state.</summary>
    public (List<long>[] FreeLists, long DataRegionEnd, int FreeBlockCount) Snapshot()
    {
        var snapshot = new List<long>[FormatConstants.OrderCount];
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            snapshot[i] = new List<long>(_freeLists[i]);
        return (snapshot, _dataRegionEnd, FreeBlockCount);
    }

    /// <summary>Restores free lists from a snapshot (for rollback).</summary>
    public void RestoreFromSnapshot(List<long>[] freeLists, long dataRegionEnd, int freeBlockCount)
    {
        for (int i = 0; i < FormatConstants.OrderCount; i++)
        {
            _freeLists[i].Clear();
            _freeLists[i].AddRange(freeLists[i]);
        }
        _dataRegionEnd = dataRegionEnd;
        FreeBlockCount = freeBlockCount;
    }

    /// <summary>Legacy snapshot support — creates a snapshot returning free list heads (for backward compat).</summary>
    public long[] SnapshotFreeListHeads()
    {
        // Return heads (first element of each list, or 0 if empty) for compatibility
        var heads = new long[FormatConstants.OrderCount];
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            heads[i] = _freeLists[i].Count > 0 ? _freeLists[i][0] : 0;
        return heads;
    }

    /// <summary>Restores from a legacy snapshot (heads + data region end).</summary>
    public void RestoreFromSnapshot(long[] snapshot, long dataRegionEnd, int freeBlockCount)
    {
        // This is used by TransactionManager — we need proper snapshot now
        // For backward compat, just restore basics
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            _freeLists[i].Clear();
        _dataRegionEnd = dataRegionEnd;
        FreeBlockCount = freeBlockCount;
    }

    /// <summary>Gets the address of the free list head for a given order (0 if empty).</summary>
    public long GetFreeListHead(int order) => _freeLists[order].Count > 0 ? _freeLists[order][0] : 0;

    private long PopFreeBlock(int order)
    {
        var list = _freeLists[order];
        if (list.Count == 0)
            throw new InvalidOperationException($"Free list for order {order} is empty.");

        // Pop from end for O(1) performance
        long address = list[^1];
        list.RemoveAt(list.Count - 1);
        FreeBlockCount--;
        return address;
    }

    private void PushFreeBlock(int order, long address)
    {
        _freeLists[order].Add(address);
        FreeBlockCount++;
    }

    private bool TryRemoveFromFreeList(int order, long targetAddress)
    {
        var list = _freeLists[order];
        int idx = list.IndexOf(targetAddress);
        if (idx < 0) return false;

        // Swap with last for O(1) removal
        list[idx] = list[^1];
        list.RemoveAt(list.Count - 1);
        FreeBlockCount--;
        return true;
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
        for (int i = 0; i < FormatConstants.OrderCount; i++)
            _freeLists[i].Clear();
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
        // and free everything else.
        Span<byte> header = stackalloc byte[4];
        long pos = dataStart;
        while (pos < maxAllocated)
        {
            file.ReadRaw(pos, header);
            int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);

            if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            {
                pos += FormatConstants.MinBlockSize;
                continue;
            }

            int order = 0;
            int sz = FormatConstants.MinBlockSize;
            while (sz < blockSize && order < FormatConstants.OrderCount - 1) { order++; sz <<= 1; }

            if (!reachableBlocks.Contains(pos))
            {
                PushFreeBlock(order, pos);
            }

            pos += blockSize;
        }
    }
}
