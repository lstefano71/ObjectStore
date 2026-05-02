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
    private readonly bool _readOnly;

    public ContainerFile File => _file;
    public BuddyAllocator Allocator => _allocator;
    public BTree Tree => _tree;
    public ulong NextNodeId => _nextNodeId;
    public TransactionManager Transactions => _txn;
    internal SuperblockManager SuperblockManager => _sbManager;
    public bool IsReadOnly => _readOnly;

    private ObjectEngine(ContainerFile file, BuddyAllocator allocator, BTree tree,
                         SuperblockManager sbManager, ulong nextNodeId, bool readOnly = false)
    {
        _file = file;
        _allocator = allocator;
        _tree = tree;
        _sbManager = sbManager;
        _nextNodeId = nextNodeId;
        _txn = new TransactionManager(this);
        _readOnly = readOnly;
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
        var engine = new ObjectEngine(file, allocator, tree, sbManager, active.NextNodeId);

        // Set dirty flag on open (cleared on clean close)
        engine.SetDirtyFlag(true);

        return engine;
    }

    /// <summary>Opens a container in read-only mode. No writes are allowed.</summary>
    public static ObjectEngine OpenReadOnly(string path)
    {
        var file = ContainerFile.Open(path, readOnly: true);
        var sbManager = new SuperblockManager(file);

        if (!sbManager.TryLoad())
            throw new InvalidOperationException("Container file has no valid superblock.");

        var active = sbManager.Active;
        if (!active.IsVersionCompatible)
            throw new IncompatibleVersionException(active.VersionMajor, active.VersionMinor);

        var allocator = new BuddyAllocator(file, FormatConstants.DataRegionOffset);

        if (active.BuddyRootAddress != 0)
        {
            int buddyOrder = ReadBlockOrderFromHeader(file, (long)active.BuddyRootAddress);
            var buddyData = file.ReadBlock((long)active.BuddyRootAddress, buddyOrder);
            allocator.Deserialize(buddyData);
        }

        var tree = new BTree(allocator, file, (long)active.BTreeRootAddress);
        return new ObjectEngine(file, allocator, tree, sbManager, active.NextNodeId, readOnly: true);
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
        ThrowIfReadOnly();
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
        ThrowIfReadOnly();
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
        ThrowIfReadOnly();
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
        ThrowIfReadOnly();
        var (key, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
        if (offset + data.Length > record.Size)
            throw new ArgumentException("WriteAt cannot extend the object. Use Append instead.");

        var extents = LoadExtentList(record);
        long currentOffset = 0;
        long writeEnd = offset + data.Length;

        for (int i = 0; i < extents.Extents.Count && currentOffset < writeEnd; i++)
        {
            var (addr, order) = extents.Extents[i];
            int capacity = FormatConstants.PayloadSizeForOrder(order);
            long extentEnd = currentOffset + capacity;

            if (offset < extentEnd && currentOffset < writeEnd)
            {
                int skipInExtent = (int)Math.Max(0, offset - currentOffset);
                int srcStart = (int)Math.Max(0, currentOffset - offset);
                int toWrite = Math.Min(capacity - skipInExtent, data.Length - srcStart);

                if (toWrite > 0)
                {
                    // COW: read old block, modify, write to new block
                    byte[] blockData = _file.ReadBlock(addr, order);
                    data.Slice(srcStart, toWrite).CopyTo(blockData.AsSpan(skipInExtent));

                    long newAddr = _allocator.Allocate(order);
                    _file.WriteBlock(newAddr, order, blockData);
                    DeferredFree(addr, order);
                    extents.Extents[i] = (newAddr, order);
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
        ThrowIfReadOnly();
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

    // --- Hierarchy ---

    private PathResolver? _pathResolver;
    private PathResolver PathResolver => _pathResolver ??= new PathResolver(this);

    /// <summary>Creates a child node under a given parent.</summary>
    public ulong CreateChild(ulong parentId, string name, bool isContainer = false)
    {
        ThrowIfReadOnly();
        ulong id = _nextNodeId++;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte flags = NodeRecord.FlagHasData;
        if (isContainer) flags = NodeRecord.FlagHasChildren;

        var record = new NodeRecord
        {
            Id = id,
            ParentId = parentId,
            NameHash = FnvHash.ComputeString(name),
            Name = name,
            NodeTypeFlags = flags,
            Size = 0,
            Created = now,
            Modified = now,
        };

        var key = new BTreeKey(record.ParentId, record.NameHash);
        _tree.Insert(key, record.Serialize());

        // Update parent's child count
        UpdateChildCount(parentId, 1);

        AutoCommit();
        return id;
    }

    /// <summary>Moves a node to a new parent (and optionally renames it).</summary>
    public void MoveNode(ulong nodeId, ulong newParentId, string? newName = null)
    {
        ThrowIfReadOnly();
        var (oldKey, record) = FindById(nodeId);
        if (record == null) throw new ObjectNotFoundException($"Node {nodeId} not found.");

        // Cycle detection: ensure newParentId is not a descendant of nodeId
        if (IsDescendantOf(newParentId, nodeId))
            throw new InvalidOperationException("Cannot move a node into its own subtree.");

        ulong oldParentId = record.ParentId;

        // Remove from old position
        _tree.Delete(oldKey);

        // Update record
        record.ParentId = newParentId;
        if (newName != null)
        {
            record.Name = newName;
            record.NameHash = FnvHash.ComputeString(newName);
        }
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Insert at new position
        var newKey = new BTreeKey(record.ParentId, record.NameHash);
        _tree.Insert(newKey, record.Serialize());

        // Update child counts
        UpdateChildCount(oldParentId, -1);
        UpdateChildCount(newParentId, 1);

        AutoCommit();
    }

    /// <summary>Recursively deletes a node and all its descendants.</summary>
    public void DeleteSubtree(ulong nodeId)
    {
        ThrowIfReadOnly();
        // Recursively collect all descendants
        var toDelete = new List<(BTreeKey Key, NodeRecord Record)>();
        CollectSubtree(nodeId, toDelete);

        // Also find the node itself
        var (nodeKey, nodeRecord) = FindById(nodeId);
        if (nodeRecord == null) throw new ObjectNotFoundException($"Node {nodeId} not found.");

        ulong parentId = nodeRecord.ParentId;

        // Delete all descendants first
        foreach (var (key, rec) in toDelete)
        {
            FreeNodeData(rec);
            _tree.Delete(key);
        }

        // Delete the node itself
        FreeNodeData(nodeRecord);
        _tree.Delete(nodeKey);

        // Update parent's child count
        UpdateChildCount(parentId, -1);

        AutoCommit();
    }

    /// <summary>Resolves a path to a node ID.</summary>
    public ulong? ResolvePath(string path) => PathResolver.Resolve(path);

    /// <summary>Lists children of a node.</summary>
    public IEnumerable<NodeRecord> ListChildren(ulong parentId) => PathResolver.ListChildren(parentId);

    /// <summary>Gets a node by path.</summary>
    public NodeRecord? GetNodeByPath(string path)
    {
        var id = ResolvePath(path);
        if (id == null) return null;
        return GetInfo(id.Value);
    }

    private void UpdateChildCount(ulong parentId, int delta)
    {
        if (parentId == 0) return; // no-op for root's parent
        var (parentKey, parentRecord) = FindById(parentId);
        if (parentRecord == null) return;

        parentRecord.ChildCount = (uint)Math.Max(0, (int)parentRecord.ChildCount + delta);
        if (parentRecord.ChildCount > 0)
            parentRecord.NodeTypeFlags |= NodeRecord.FlagHasChildren;
        else
            parentRecord.NodeTypeFlags &= unchecked((byte)~NodeRecord.FlagHasChildren);

        parentRecord.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tree.Update(parentKey, parentRecord.Serialize());
    }

    private bool IsDescendantOf(ulong candidateId, ulong ancestorId)
    {
        if (candidateId == ancestorId) return true;
        // Walk up from candidate to root
        ulong current = candidateId;
        int maxDepth = 1000; // prevent infinite loops
        while (current != 0 && current != 1 && maxDepth-- > 0)
        {
            var (_, record) = FindById(current);
            if (record == null) return false;
            if (record.ParentId == ancestorId) return true;
            current = record.ParentId;
        }
        return false;
    }

    private void CollectSubtree(ulong nodeId, List<(BTreeKey, NodeRecord)> results)
    {
        foreach (var (key, value) in _tree.RangeScan(nodeId))
        {
            var record = NodeRecord.Deserialize(value);
            if (record.IsDeleted) continue;
            results.Add((key, record));
            if (record.HasChildren)
                CollectSubtree(record.Id, results);
        }
    }

    private void FreeNodeData(NodeRecord record)
    {
        // Free extent data
        if (record.ExtentListAddress != 0)
        {
            var extents = LoadExtentList(record);
            foreach (var (addr, order) in extents.Extents)
                DeferredFree(addr, order);
            DeferredFreeExtentListBlock(record.ExtentListAddress);
        }
        // Free metadata
        if (record.MetadataBlockAddress != 0)
            DeferredFreeExtentListBlock(record.MetadataBlockAddress);
    }

    // --- Transaction support methods ---

    /// <summary>Begins an explicit transaction.</summary>
    public void BeginTransaction()
    {
        ThrowIfReadOnly();
        _txn.Begin();
    }

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

    // --- Metadata ---

    /// <summary>Sets a metadata key-value pair on an object.</summary>
    public void SetMetadata(ulong id, string key, string value)
    {
        ThrowIfReadOnly();
        var (treeKey, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");

        var metadata = LoadMetadata(record);
        metadata[key] = value;
        record.MetadataBlockAddress = SaveMetadata(metadata, record.MetadataBlockAddress);
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tree.Update(treeKey, record.Serialize());
        AutoCommit();
    }

    /// <summary>Gets a metadata value by key, or null if not found.</summary>
    public string? GetMetadata(ulong id, string key)
    {
        var (_, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");

        var metadata = LoadMetadata(record);
        return metadata.GetValueOrDefault(key);
    }

    /// <summary>Deletes a metadata key. Returns true if the key existed.</summary>
    public bool DeleteMetadata(ulong id, string key)
    {
        ThrowIfReadOnly();
        var (treeKey, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");

        var metadata = LoadMetadata(record);
        if (!metadata.Remove(key)) return false;

        record.MetadataBlockAddress = SaveMetadata(metadata, record.MetadataBlockAddress);
        record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tree.Update(treeKey, record.Serialize());
        AutoCommit();
        return true;
    }

    private Dictionary<string, string> LoadMetadata(NodeRecord record)
    {
        if (record.MetadataBlockAddress == 0)
            return new Dictionary<string, string>();

        int order = GetBlockOrderFromHeader(record.MetadataBlockAddress);
        byte[] data = _file.ReadBlock(record.MetadataBlockAddress, order);
        return DeserializeMetadata(data);
    }

    private long SaveMetadata(Dictionary<string, string> metadata, long oldAddress)
    {
        byte[] data = SerializeMetadata(metadata);
        int order = FormatConstants.OrderForPayload(data.Length);

        if (oldAddress != 0)
            DeferredFreeExtentListBlock(oldAddress); // reuses the same free helper

        long addr = _allocator.Allocate(order);
        _file.WriteBlock(addr, order, data);
        return addr;
    }

    private static byte[] SerializeMetadata(Dictionary<string, string> metadata)
    {
        // Format: [count:u16] [key_len:u16 key_bytes value_len:u16 value_bytes]...
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)metadata.Count);
        foreach (var (key, value) in metadata)
        {
            byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
            byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
            bw.Write((ushort)keyBytes.Length);
            bw.Write(keyBytes);
            bw.Write((ushort)valueBytes.Length);
            bw.Write(valueBytes);
        }
        return ms.ToArray();
    }

    private static Dictionary<string, string> DeserializeMetadata(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>();
        if (data.Length < 2) return result;

        int offset = 0;
        ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;

        for (int i = 0; i < count && offset < data.Length; i++)
        {
            ushort keyLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            string key = System.Text.Encoding.UTF8.GetString(data.Slice(offset, keyLen));
            offset += keyLen;
            ushort valueLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            string value = System.Text.Encoding.UTF8.GetString(data.Slice(offset, valueLen));
            offset += valueLen;
            result[key] = value;
        }
        return result;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Clear dirty flag on clean close (skip for read-only)
        if (!_readOnly)
            try { SetDirtyFlag(false); } catch { /* best-effort */ }
        _file.Dispose();
    }

    /// <summary>Sets or clears the dirty-open flag and commits the superblock.</summary>
    internal void SetDirtyFlag(bool dirty)
    {
        var sb = _sbManager.Active;
        if (dirty)
            sb.Flags |= Superblock.FlagDirtyOpen;
        else
            sb.Flags &= ~Superblock.FlagDirtyOpen;
        _sbManager.Commit(sb);
    }

    /// <summary>Returns true if the store was opened with the dirty flag set.</summary>
    public bool WasDirtyOnOpen()
    {
        // Dirty flag is set immediately on open, so check current state
        return (_sbManager.Active.Flags & Superblock.FlagDirtyOpen) != 0;
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

    private void ThrowIfReadOnly()
    {
        if (_readOnly)
            throw new ReadOnlyContainerException();
    }
}
