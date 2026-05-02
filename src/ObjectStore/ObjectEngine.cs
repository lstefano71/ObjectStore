namespace ObjectStore;

/// <summary>
/// Core object storage engine. Manages objects within the container using
/// the buddy allocator and B-tree. Supports explicit transactions with
/// savepoint nesting. Operations auto-commit when no explicit transaction is active.
/// </summary>
public sealed class ObjectEngine : IDisposable
{
    private readonly ContainerFile _file;
    private readonly BuddyAllocator _allocator;
    private BTree _tree;
    private readonly SuperblockManager _sbManager;
    private ulong _nextNodeId;
    private readonly TransactionManager _txn;

    public ContainerFile File => _file;
    public BuddyAllocator Allocator => _allocator;
    public BTree Tree => _tree;
    public ulong NextNodeId => _nextNodeId;
    public TransactionManager Transactions => _txn;

    private ObjectEngine(ContainerFile file, BuddyAllocator allocator, BTree tree,
                         SuperblockManager sbManager, ulong nextNodeId)
    {
        _file = file;
        _allocator = allocator;
        _tree = tree;
        _sbManager = sbManager;
        _nextNodeId = nextNodeId;
        _txn = new TransactionManager(this);
    }

    /// <summary>Creates a new container.</summary>
    public static ObjectEngine Create(string path)
    {
        var file = ContainerFile.Create(path);
        file.EnsureSize(FormatConstants.DataRegionOffset);

        var allocator = new BuddyAllocator(file, FormatConstants.DataRegionOffset);
        var tree = new BTree(allocator, file);
        var sbManager = new SuperblockManager(file);

        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
            ContainerSize = (ulong)file.FileSize,
            NextNodeId = 2, // 1 is reserved for root
            RootNodeId = 1,
        };
        sbManager.Initialize(sb);

        return new ObjectEngine(file, allocator, tree, sbManager, 2);
    }

    /// <summary>Opens an existing container.</summary>
    public static ObjectEngine Open(string path)
    {
        var file = ContainerFile.Open(path);
        var sbManager = new SuperblockManager(file);

        if (!sbManager.TryLoad())
            throw new InvalidOperationException("Container file has no valid superblock.");

        var active = sbManager.Active;
        if (!active.IsVersionCompatible)
            throw new IncompatibleVersionException(active.VersionMajor, active.VersionMinor);

        var allocator = new BuddyAllocator(file, FormatConstants.DataRegionOffset);

        // Load buddy state if stored
        if (active.BuddyRootAddress != 0)
        {
            int buddyOrder = ReadBlockOrderFromHeader(file, (long)active.BuddyRootAddress);
            var buddyData = file.ReadBlock((long)active.BuddyRootAddress, buddyOrder);
            allocator.Deserialize(buddyData);
        }

        var tree = new BTree(allocator, file, (long)active.BTreeRootAddress);
        return new ObjectEngine(file, allocator, tree, sbManager, active.NextNodeId);
    }

    /// <summary>Opens or creates a container.</summary>
    public static ObjectEngine OpenOrCreate(string path)
    {
        if (System.IO.File.Exists(path))
            return Open(path);
        return Create(path);
    }

    /// <summary>Creates a new object with optional name. Returns the assigned ID.</summary>
    public ulong CreateObject(string? name = null, byte compressionCodec = 0)
    {
        ulong id = _nextNodeId++;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var record = new NodeRecord
        {
            Id = id,
            ParentId = 1, // Under root by default
            NameHash = name != null ? FnvHash.ComputeString(name) : 0,
            Name = name ?? string.Empty,
            NodeTypeFlags = NodeRecord.FlagHasData,
            Size = 0,
            Created = now,
            Modified = now,
            CompressionCodec = compressionCodec,
        };

        var key = new BTreeKey(record.ParentId, record.NameHash != 0 ? record.NameHash : id);
        _tree.Insert(key, record.Serialize());
        AutoCommit();
        return id;
    }

    /// <summary>Deletes an object by ID.</summary>
    public bool DeleteObject(ulong id)
    {
        var (key, record) = FindById(id);
        if (record == null) return false;

        // Free extent data
        if (record.ExtentListAddress != 0)
        {
            var extents = LoadExtentList(record);
            foreach (var (addr, order) in extents.Extents)
                DeferredFree(addr, order);
            DeferredFreeExtentListBlock(record.ExtentListAddress);
        }

        _tree.Delete(key);
        AutoCommit();
        return true;
    }

    /// <summary>Checks if an object exists by ID.</summary>
    public bool Exists(ulong id)
    {
        var (_, record) = FindById(id);
        return record != null;
    }

    /// <summary>Gets the NodeRecord for an object by ID.</summary>
    public NodeRecord? GetInfo(ulong id)
    {
        var (_, record) = FindById(id);
        return record;
    }

    /// <summary>Appends data to an object.</summary>
    public void Append(ulong id, ReadOnlySpan<byte> data)
    {
        var (key, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");

        // Load or create extent list
        var extents = LoadExtentList(record);

        int remaining = data.Length;
        int dataOffset = 0;

        // Try to fill the last extent block first (COW it with appended data)
        if (extents.Extents.Count > 0)
        {
            int lastIdx = extents.Extents.Count - 1;
            var (lastAddr, lastOrder) = extents.Extents[lastIdx];
            int capacity = FormatConstants.PayloadSizeForOrder(lastOrder);

            // Calculate how much of the last block is used
            long usedBefore = 0;
            for (int i = 0; i < lastIdx; i++)
                usedBefore += FormatConstants.PayloadSizeForOrder(extents.Extents[i].Order);
            int lastBlockUsed = (int)(record.Size - usedBefore);

            int spaceInLast = capacity - lastBlockUsed;
            if (spaceInLast > 0)
            {
                int toFill = Math.Min(remaining, spaceInLast);
                byte[] blockData = _file.ReadBlock(lastAddr, lastOrder);
                data.Slice(dataOffset, toFill).CopyTo(blockData.AsSpan(lastBlockUsed));

                // COW: allocate new block, write merged data, free old
                long newAddr = _allocator.Allocate(lastOrder);
                _file.WriteBlock(newAddr, lastOrder, blockData.AsSpan(0, lastBlockUsed + toFill));
                DeferredFree(lastAddr, lastOrder);
                extents.Extents[lastIdx] = (newAddr, lastOrder);

                dataOffset += toFill;
                remaining -= toFill;
            }
        }

        // Allocate new blocks for remaining data
        while (remaining > 0)
        {
            int order = FormatConstants.OrderForPayload(Math.Min(remaining, FormatConstants.PayloadSizeForOrder(FormatConstants.OrderCount - 1)));
            int capacity = FormatConstants.PayloadSizeForOrder(order);
            int toWrite = Math.Min(remaining, capacity);

            long blockAddr = _allocator.Allocate(order);
            _file.WriteBlock(blockAddr, order, data.Slice(dataOffset, toWrite));
            extents.Extents.Add((blockAddr, order));

            dataOffset += toWrite;
            remaining -= toWrite;
        }

        // Save extent list
        record.ExtentListAddress = SaveExtentList(extents, record.ExtentListAddress);
        record.Size += data.Length;
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _tree.Update(key, record.Serialize());
        AutoCommit();
    }

    /// <summary>Reads data from an object at the given offset.</summary>
    public int ReadAt(ulong id, long offset, Span<byte> buffer)
    {
        var (_, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
        if (offset >= record.Size) return 0;

        var extents = LoadExtentList(record);
        int bytesRead = 0;
        long currentOffset = 0;

        foreach (var (addr, order) in extents.Extents)
        {
            int capacity = FormatConstants.PayloadSizeForOrder(order);
            long extentEnd = currentOffset + capacity;

            if (offset < extentEnd && currentOffset < offset + buffer.Length)
            {
                // This extent overlaps with our read range
                int skipInExtent = (int)Math.Max(0, offset - currentOffset);
                int startInBuffer = (int)Math.Max(0, currentOffset - offset);
                int toRead = Math.Min(capacity - skipInExtent, buffer.Length - startInBuffer);
                toRead = (int)Math.Min(toRead, record.Size - (currentOffset + skipInExtent));

                if (toRead > 0)
                {
                    byte[] blockData = _file.ReadBlock(addr, order);
                    blockData.AsSpan(skipInExtent, toRead).CopyTo(buffer[startInBuffer..]);
                    bytesRead += toRead;
                }
            }

            currentOffset += capacity;
            if (currentOffset >= offset + buffer.Length || currentOffset >= record.Size)
                break;
        }

        return bytesRead;
    }

    /// <summary>Overwrites data at a specific offset (same-size only).</summary>
    public void WriteAt(ulong id, long offset, ReadOnlySpan<byte> data)
    {
        var (key, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
        if (offset + data.Length > record.Size)
            throw new ArgumentException("WriteAt cannot extend the object. Use Append instead.");

        var extents = LoadExtentList(record);
        long currentOffset = 0;
        int dataOffset = 0;
        int remaining = data.Length;

        for (int i = 0; i < extents.Extents.Count && remaining > 0; i++)
        {
            var (addr, order) = extents.Extents[i];
            int capacity = FormatConstants.PayloadSizeForOrder(order);
            long extentEnd = currentOffset + capacity;

            if (offset < extentEnd && currentOffset < offset + data.Length)
            {
                int skipInExtent = (int)Math.Max(0, offset - currentOffset);
                int startInData = (int)Math.Max(0, currentOffset - offset);
                int toWrite = Math.Min(capacity - skipInExtent, remaining - startInData + dataOffset);
                toWrite = Math.Min(toWrite, data.Length - startInData);

                if (toWrite > 0)
                {
                    // COW: read old block, modify, write to new block
                    byte[] blockData = _file.ReadBlock(addr, order);
                    data.Slice(startInData, toWrite).CopyTo(blockData.AsSpan(skipInExtent));

                    long newAddr = _allocator.Allocate(order);
                    _file.WriteBlock(newAddr, order, blockData);
                    DeferredFree(addr, order);
                    extents.Extents[i] = (newAddr, order);
                    remaining -= toWrite;
                }
            }

            currentOffset += capacity;
        }

        record.ExtentListAddress = SaveExtentList(extents, record.ExtentListAddress);
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tree.Update(key, record.Serialize());
        AutoCommit();
    }

    /// <summary>Truncates an object to the specified length.</summary>
    public void Truncate(ulong id, long newLength)
    {
        var (key, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
        if (newLength >= record.Size) return;

        var extents = LoadExtentList(record);

        for (int i = extents.Extents.Count - 1; i >= 0; i--)
        {
            long extentStart = 0;
            for (int j = 0; j < i; j++)
                extentStart += FormatConstants.PayloadSizeForOrder(extents.Extents[j].Order);

            if (extentStart >= newLength)
            {
                DeferredFree(extents.Extents[i].Address, extents.Extents[i].Order);
                extents.Extents.RemoveAt(i);
            }
        }

        record.Size = newLength;
        record.ExtentListAddress = SaveExtentList(extents, record.ExtentListAddress);
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tree.Update(key, record.Serialize());
        AutoCommit();
    }

    /// <summary>Lists all objects.</summary>
    public IEnumerable<NodeRecord> ListObjects()
    {
        foreach (var (_, value) in _tree.ScanAll())
            yield return NodeRecord.Deserialize(value);
    }

    // --- Transaction support methods ---

    /// <summary>Begins an explicit transaction.</summary>
    public void BeginTransaction() => _txn.Begin();

    /// <summary>Commits the current transaction.</summary>
    public void CommitTransaction() => _txn.Commit();

    /// <summary>Rolls back the current transaction.</summary>
    public void RollbackTransaction() => _txn.Rollback();

    /// <summary>Auto-commits if no explicit transaction is active.</summary>
    private void AutoCommit()
    {
        if (!_txn.HasActiveTransaction)
            CommitInternal();
    }

    /// <summary>Persists current state to superblock (called by TransactionManager on commit).</summary>
    internal void CommitInternal()
    {
        // Save buddy state
        byte[] buddyState = _allocator.Serialize();
        int buddyOrder = FormatConstants.OrderForPayload(buddyState.Length);
        long buddyAddr = _allocator.Allocate(buddyOrder);
        _file.WriteBlock(buddyAddr, buddyOrder, buddyState);

        var sb = _sbManager.Active;
        sb.BTreeRootAddress = (ulong)_tree.RootAddress;
        sb.BuddyRootAddress = (ulong)buddyAddr;
        sb.ContainerSize = (ulong)_file.FileSize;
        sb.NextNodeId = _nextNodeId;
        _sbManager.Commit(sb);

        _tree.FreedBlocks.Clear();
    }

    /// <summary>Restores B-tree root (used by TransactionManager on rollback).</summary>
    internal void RestoreRootAddress(long address)
    {
        _tree = new BTree(_allocator, _file, address, _tree.Order);
    }

    /// <summary>Restores the next node ID counter (used by TransactionManager on rollback).</summary>
    internal void RestoreNextNodeId(ulong id) => _nextNodeId = id;

    public void Dispose()
    {
        _file.Dispose();
    }

    /// <summary>
    /// Frees a block. During a transaction, defers the free to commit time
    /// to avoid corrupting block data (needed for rollback).
    /// </summary>
    private void DeferredFree(long address, int order)
    {
        if (_txn.HasActiveTransaction)
            _txn.TrackPendingFree(address, order);
        else
            _allocator.Free(address, order);
    }

    private (BTreeKey Key, NodeRecord? Record) FindById(ulong id)
    {
        foreach (var (key, value) in _tree.ScanAll())
        {
            var record = NodeRecord.Deserialize(value);
            if (record.Id == id)
                return (key, record);
        }
        return (default, null);
    }

    private ExtentList LoadExtentList(NodeRecord record)
    {
        if (record.ExtentListAddress == 0)
            return new ExtentList();
        int order = GetBlockOrderFromHeader(record.ExtentListAddress);
        byte[] data = _file.ReadBlock(record.ExtentListAddress, order);
        return ExtentList.Deserialize(data);
    }

    private int GetBlockOrderFromHeader(long address)
    {
        return ReadBlockOrderFromHeader(_file, address);
    }

    private static int ReadBlockOrderFromHeader(ContainerFile file, long address)
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

    private long SaveExtentList(ExtentList extents, long oldAddress)
    {
        byte[] data = extents.Serialize();
        int order = FormatConstants.OrderForPayload(data.Length);

        if (oldAddress != 0)
        {
            DeferredFreeExtentListBlock(oldAddress);
        }

        long addr = _allocator.Allocate(order);
        _file.WriteBlock(addr, order, data);
        return addr;
    }

    private void DeferredFreeExtentListBlock(long address)
    {
        int order = GetBlockOrderFromAddress(address);
        DeferredFree(address, order);
    }

    private int GetBlockOrderFromAddress(long address)
    {
        Span<byte> header = stackalloc byte[4];
        _file.ReadRaw(address, header);
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
