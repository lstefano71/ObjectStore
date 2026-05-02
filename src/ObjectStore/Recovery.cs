namespace ObjectStore;

/// <summary>
/// Recovers a container after a dirty close by performing a reachability scan
/// from the committed B-tree root and rebuilding the buddy allocator free lists.
/// </summary>
public static class Recovery
{
    /// <summary>
    /// Performs recovery on a container. Scans the committed B-tree to find
    /// all reachable blocks, then rebuilds the buddy allocator to reclaim any
    /// leaked blocks not reachable from the committed state.
    /// Returns true if recovery was needed (dirty-close flag was set).
    /// </summary>
    public static bool Recover(ObjectEngine engine)
    {
        var sb = engine.SuperblockManager.Active;
        bool wasDirty = (sb.Flags & Superblock.FlagDirtyOpen) != 0;

        // Collect all reachable block addresses
        var reachableBlocks = new HashSet<long>();

        // 1. Buddy state block
        if (sb.BuddyRootAddress != 0)
            reachableBlocks.Add((long)sb.BuddyRootAddress);

        // 2. B-tree nodes and all data referenced by objects
        CollectBTreeBlocks(engine, (long)sb.BTreeRootAddress, reachableBlocks);

        // 3. Rebuild the allocator: reset free lists, mark non-reachable blocks as free
        RebuildAllocator(engine, reachableBlocks);

        // Clear dirty flag and commit
        sb.Flags &= ~Superblock.FlagDirtyOpen;
        engine.CommitInternal();

        return wasDirty;
    }

    /// <summary>
    /// Recursively collects all B-tree node blocks and all data blocks
    /// (extent lists + data extents + metadata blocks) referenced by leaf records.
    /// </summary>
    private static void CollectBTreeBlocks(ObjectEngine engine, long nodeAddress, HashSet<long> reachable)
    {
        if (nodeAddress == 0) return;

        reachable.Add(nodeAddress);

        // Read the B-tree node
        int order = ReadBlockOrder(engine.File, nodeAddress);
        byte[] nodeData = engine.File.ReadBlock(nodeAddress, order);
        var node = BTreeNode.Deserialize(nodeData);

        if (node.IsLeaf)
        {
            // Scan leaf values for NodeRecords → track their extent lists + data blocks
            foreach (var value in node.Values)
            {
                var record = NodeRecord.Deserialize(value);
                CollectObjectBlocks(engine, record, reachable);
            }
        }
        else
        {
            // Internal node: recurse into children
            foreach (long childAddr in node.Children)
            {
                CollectBTreeBlocks(engine, childAddr, reachable);
            }
        }
    }

    /// <summary>
    /// Collects all blocks owned by an object (extent list block + data extent blocks + metadata block).
    /// </summary>
    private static void CollectObjectBlocks(ObjectEngine engine, NodeRecord record, HashSet<long> reachable)
    {
        // Extent list block
        if (record.ExtentListAddress != 0)
        {
            reachable.Add(record.ExtentListAddress);
            int elOrder = ReadBlockOrder(engine.File, record.ExtentListAddress);
            byte[] elData = engine.File.ReadBlock(record.ExtentListAddress, elOrder);
            var extents = ExtentList.Deserialize(elData);

            // Data blocks
            foreach (var (addr, _) in extents.Extents)
                reachable.Add(addr);
        }

        // Metadata block
        if (record.MetadataBlockAddress != 0)
            reachable.Add(record.MetadataBlockAddress);
    }

    /// <summary>
    /// Rebuilds the buddy allocator by scanning the data region and
    /// freeing all blocks that are not in the reachable set.
    /// This is done by creating a fresh allocator and marking all
    /// reachable blocks as allocated.
    /// </summary>
    private static void RebuildAllocator(ObjectEngine engine, HashSet<long> reachableBlocks)
    {
        var file = engine.File;
        var allocator = engine.Allocator;

        // Reset the allocator to empty state
        allocator.Reset();

        // Walk the data region finding all block boundaries
        // We scan from DataRegionOffset to end of file, identifying allocated regions
        long dataStart = FormatConstants.DataRegionOffset;
        long fileSize = file.FileSize;

        // Find the maximum extent of allocated blocks
        long maxAllocated = dataStart;
        foreach (long addr in reachableBlocks)
        {
            int order = ReadBlockOrder(file, addr);
            long end = addr + FormatConstants.BlockSizeForOrder(order);
            if (end > maxAllocated)
                maxAllocated = end;
        }

        // Rebuild: allocate from dataStart up to maxAllocated,
        // then free non-reachable blocks
        allocator.RebuildFromReachable(dataStart, maxAllocated, reachableBlocks, file);
    }

    private static int ReadBlockOrder(ContainerFile file, long address)
    {
        Span<byte> header = stackalloc byte[4];
        file.ReadRaw(address, header);
        int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        int order = 0;
        int sz = FormatConstants.MinBlockSize;
        while (sz < blockSize && order < FormatConstants.OrderCount - 1)
        {
            order++;
            sz <<= 1;
        }
        return order;
    }
}
