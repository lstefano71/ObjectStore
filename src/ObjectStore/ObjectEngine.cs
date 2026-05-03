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
    private BTree _idTree; // Secondary B-tree: keyed by (0, objectId), value = serialized primary BTreeKey
    private readonly SuperblockManager _sbManager;
    private ulong _nextNodeId;
    private readonly TransactionManager _txn;
    private readonly bool _readOnly;
    private int _lastBuddyBlockOrder; // order of the last committed buddy state block
    private Dictionary<ulong, BTreeKey>? _idTreeFallback; // read-only migration fallback
    private TimeSpan _lockTimeout = TimeSpan.FromSeconds(30);
    private bool _writeLockHeld;
    private ulong _lastKnownGeneration; // tracks generation we last committed or refreshed to
    private readonly List<long> _pendingBTreeFrees = new(); // B-tree freed blocks not yet persisted

    // ID→(PrimaryKey, NodeRecord) lookup cache to avoid double B-tree traversal
    private const int IdCacheCapacity = 1024;
    private readonly Dictionary<ulong, (BTreeKey Key, NodeRecord Record)> _idCache = new();

    /// <summary>
    /// When true, read operations automatically check the superblock generation
    /// and refresh if another process has committed. Required for multi-process readers.
    /// </summary>
    public bool MultiProcessMode { get; set; }

    public ContainerFile File => _file;
    public BuddyAllocator Allocator => _allocator;
    public BTree Tree => _tree;
    public BTree IdTree => _idTree;
    public ulong NextNodeId => _nextNodeId;
    public TransactionManager Transactions => _txn;
    internal SuperblockManager SuperblockManager => _sbManager;
    internal BTree PrimaryTree => _tree;
    public bool IsReadOnly => _readOnly;

    private ObjectEngine(ContainerFile file, BuddyAllocator allocator, BTree tree,
                         BTree idTree, SuperblockManager sbManager, ulong nextNodeId,
                         bool readOnly = false, int lastBuddyBlockOrder = 0)
    {
        _file = file;
        _allocator = allocator;
        _tree = tree;
        _idTree = idTree;
        _sbManager = sbManager;
        _nextNodeId = nextNodeId;
        _txn = new TransactionManager(this);
        _readOnly = readOnly;
        _lastBuddyBlockOrder = lastBuddyBlockOrder;
        _lastKnownGeneration = sbManager.Active.Generation;
    }

    /// <summary>Creates a new container.</summary>
    public static ObjectEngine Create(string path)
    {
        var file = ContainerFile.Create(path);
        file.EnsureSize(FormatConstants.DataRegionOffset);

        var allocator = new BuddyAllocator(file, FormatConstants.DataRegionOffset);
        var tree = new BTree(allocator, file);
        var idTree = new BTree(allocator, file);
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

        return new ObjectEngine(file, allocator, tree, idTree, sbManager, 2);
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
        int lastBuddyOrder = 0;

        // Load buddy state if stored
        if (active.BuddyRootAddress != 0)
        {
            lastBuddyOrder = ReadBlockOrderFromHeader(file, (long)active.BuddyRootAddress);
            var buddyData = file.ReadBlock((long)active.BuddyRootAddress, lastBuddyOrder);
            allocator.Deserialize(buddyData);
        }

        var tree = new BTree(allocator, file, (long)active.BTreeRootAddress);
        var idTree = new BTree(allocator, file, (long)active.IdIndexRootAddress);
        var engine = new ObjectEngine(file, allocator, tree, idTree, sbManager, active.NextNodeId,
            lastBuddyBlockOrder: lastBuddyOrder);

        // Migration: if no ID index exists, rebuild from primary tree
        if (active.IdIndexRootAddress == 0 && active.BTreeRootAddress != 0)
            engine.RebuildIdIndex();

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
        var idTree = new BTree(allocator, file, (long)active.IdIndexRootAddress);

        // Migration for read-only: rebuild in memory (won't persist but enables lookups)
        var engine = new ObjectEngine(file, allocator, tree, idTree, sbManager, active.NextNodeId, readOnly: true);
        if (active.IdIndexRootAddress == 0 && active.BTreeRootAddress != 0)
            engine.RebuildIdIndexReadOnly();
        return engine;
    }

    /// <summary>Opens or creates a container.</summary>
    public static ObjectEngine OpenOrCreate(string path)
    {
        if (System.IO.File.Exists(path))
            return Open(path);
        return Create(path);
    }

    /// <summary>
    /// Reloads the allocator and B-tree state from the latest committed superblock.
    /// Skips reload if we are already at the latest generation (our own last commit).
    /// </summary>
    internal void RefreshFromDisk()
    {
        // Read superblock to check generation
        if (!_sbManager.TryLoad())
            throw new InvalidOperationException("Failed to reload superblock.");

        var active = _sbManager.Active;

        // If we're already up-to-date, skip the full reload
        if (active.Generation == _lastKnownGeneration)
            return;

        _file.BlockCache?.Clear();
        IdCacheClear();

        if (active.BuddyRootAddress != 0)
        {
            _lastBuddyBlockOrder = ReadBlockOrderFromHeader(_file, (long)active.BuddyRootAddress);
            var buddyData = _file.ReadBlock((long)active.BuddyRootAddress, _lastBuddyBlockOrder);
            _allocator.Deserialize(buddyData);
        }

        // Replay pending B-tree frees that were not yet persisted to the on-disk allocator.
        // These are blocks freed after our last commit but before the allocator was serialized.
        foreach (long addr in _pendingBTreeFrees)
            _allocator.Free(addr, GetBlockOrderFromHeader(addr));

        _tree = new BTree(_allocator, _file, (long)active.BTreeRootAddress, _tree.Order);
        _idTree = new BTree(_allocator, _file, (long)active.IdIndexRootAddress, _idTree.Order);
        _nextNodeId = active.NextNodeId;
        _lastKnownGeneration = active.Generation;
    }

    /// <summary>
    /// Refreshes the engine state to see writes committed by other processes.
    /// Invalidates the block cache and reloads tree roots from the latest superblock.
    /// Safe to call from any process at any time (does not require the write lock).
    /// </summary>
    public void Refresh()
    {
        _file.RefreshFileSize();
        // Force a full reload by resetting the generation tracker
        _lastKnownGeneration = 0;
        RefreshFromDisk();
    }

    /// <summary>
    /// Lightweight staleness check: re-reads the superblock and refreshes only if
    /// another process has committed (generation changed). Called automatically
    /// before reads when MultiProcessMode is enabled.
    /// </summary>
    private void RefreshIfStale()
    {
        if (!MultiProcessMode) return;
        _file.RefreshFileSize();
        RefreshFromDisk(); // no-op if generation unchanged
    }

    /// <summary>Creates a new object with optional name. Returns the assigned ID.</summary>
    public ulong CreateObject(string? name = null, byte compressionCodec = 0)
    {
        ThrowIfReadOnly();
        try
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
            key = InsertWithCollisionHandling(key, record);

            // Insert into ID index: key=(0, id), value=serialized primary key
            var idKey = new BTreeKey(0, id);
            _idTree.Insert(idKey, key.Serialize());

            // Cache the newly-created object for fast FindById in subsequent Append
            IdCachePut(id, key, record);

            AutoCommit();
            return id;
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Deletes an object by ID.</summary>
    public bool DeleteObject(ulong id)
    {
        ThrowIfReadOnly();
        try
        {
            var (key, record) = FindById(id);
            if (record == null) return false;
            IdCacheInvalidate(id); // Invalidate before mutation

            // Free extent data
            if (record.ExtentListAddress != 0)
            {
                var extents = LoadExtentList(record);
                foreach (var (addr, order) in extents.Extents)
                    DeferredFree(addr, order);
                DeferredFreeExtentListBlock(record.ExtentListAddress);
            }

            _tree.Delete(key);
            _idTree.Delete(new BTreeKey(0, id));
            AutoCommit();
            return true;
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Checks if an object exists by ID.</summary>
    public bool Exists(ulong id)
    {
        RefreshIfStale();
        var (_, record) = FindById(id);
        return record != null;
    }

    /// <summary>Gets the NodeRecord for an object by ID.</summary>
    public NodeRecord? GetInfo(ulong id)
    {
        RefreshIfStale();
        var (_, record) = FindById(id);
        return record;
    }

    /// <summary>Appends data to an object.</summary>
    public void Append(ulong id, ReadOnlySpan<byte> data)
    {
        ThrowIfReadOnly();
        try
        {
            var (key, record) = FindById(id);
            if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
            IdCacheInvalidate(id); // Invalidate before mutation to prevent stale cache on failure

            long newSize = record.Size + data.Length;

            // Inline path: if object is currently inline (or empty) and new size fits
            if (record.HasInlineData || (record.Size == 0 && record.ExtentListAddress == 0))
            {
                if (newSize <= NodeRecord.InlineThreshold && record.CompressionCodec == 0)
                {
                    // Append to inline data
                    byte[] newInline = new byte[newSize];
                    if (record.InlineData != null)
                        record.InlineData.AsSpan().CopyTo(newInline);
                    data.CopyTo(newInline.AsSpan((int)record.Size));

                    record.InlineData = newInline;
                    record.NodeTypeFlags |= NodeRecord.FlagInlineData;
                    record.Size = newSize;
                    record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _tree.Update(key, record.Serialize());
                    AutoCommit();
                    return;
                }
                else if (record.HasInlineData)
                {
                    // Promote: inline data exceeds threshold, move to extent-based storage
                    PromoteInlineToExtents(record, key);
                    // Fall through to normal extent-based append
                }
            }

            // Normal extent-based append path
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
                    byte[] blockData = _file.ReadBlockMutable(lastAddr, lastOrder);
                    data.Slice(dataOffset, toFill).CopyTo(blockData.AsSpan(lastBlockUsed));

                    // COW: allocate new block, write merged data, free old
                    long newAddr = AllocateTracked(lastOrder);
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

                long blockAddr = AllocateTracked(order);
                _file.WriteBlock(blockAddr, order, data.Slice(dataOffset, toWrite));
                extents.Extents.Add((blockAddr, order));

                dataOffset += toWrite;
                remaining -= toWrite;
            }

            // Save extent list
            record.ExtentListAddress = SaveExtentList(extents, record.ExtentListAddress);
            record.Size = newSize;
            record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _tree.Update(key, record.Serialize());
            AutoCommit();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Reads data from an object at the given offset.</summary>
    public int ReadAt(ulong id, long offset, Span<byte> buffer)
    {
        RefreshIfStale();
        var (_, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
        if (offset >= record.Size) return 0;

        // Inline path: data stored directly in the record
        if (record.HasInlineData && record.InlineData != null)
        {
            int available = (int)(record.Size - offset);
            int toRead = Math.Min(available, buffer.Length);
            record.InlineData.AsSpan((int)offset, toRead).CopyTo(buffer);
            return toRead;
        }

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
        try
        {
            var (key, record) = FindById(id);
            if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
            if (offset + data.Length > record.Size)
                throw new ArgumentException("WriteAt cannot extend the object. Use Append instead.");
            IdCacheInvalidate(id); // Invalidate before mutation

            // Inline path
            if (record.HasInlineData && record.InlineData != null)
            {
                data.CopyTo(record.InlineData.AsSpan((int)offset));
                record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _tree.Update(key, record.Serialize());
                AutoCommit();
                return;
            }

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
                        byte[] blockData = _file.ReadBlockMutable(addr, order);
                        data.Slice(srcStart, toWrite).CopyTo(blockData.AsSpan(skipInExtent));

                        long newAddr = AllocateTracked(order);
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
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Truncates an object to the specified length.</summary>
    public void Truncate(ulong id, long newLength)
    {
        ThrowIfReadOnly();
        try
        {
            var (key, record) = FindById(id);
            if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
            if (newLength >= record.Size) return;
            IdCacheInvalidate(id); // Invalidate before mutation

            // Inline path
            if (record.HasInlineData && record.InlineData != null)
            {
                if (newLength == 0)
                {
                    record.InlineData = null;
                    record.NodeTypeFlags = (byte)(record.NodeTypeFlags & ~NodeRecord.FlagInlineData);
                }
                else
                {
                    record.InlineData = record.InlineData[..(int)newLength];
                }
                record.Size = newLength;
                record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _tree.Update(key, record.Serialize());
                AutoCommit();
                return;
            }

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
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Lists all objects.</summary>
    public IEnumerable<NodeRecord> ListObjects()
    {
        RefreshIfStale();
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
        try
        {
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
            key = InsertWithCollisionHandling(key, record);

            // Insert into ID index
            _idTree.Insert(new BTreeKey(0, id), key.Serialize());

            // Update parent's child count
            UpdateChildCount(parentId, 1);

            AutoCommit();
            return id;
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Moves a node to a new parent (and optionally renames it).</summary>
    public void MoveNode(ulong nodeId, ulong newParentId, string? newName = null)
    {
        ThrowIfReadOnly();
        try
        {
            var (oldKey, record) = FindById(nodeId);
            if (record == null) throw new ObjectNotFoundException($"Node {nodeId} not found.");
            IdCacheInvalidate(nodeId); // Invalidate before mutation

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

            // Insert at new position (with collision handling)
            var newKey = new BTreeKey(record.ParentId, record.NameHash);
            newKey = InsertWithCollisionHandling(newKey, record);

            // Update ID index to point to the new primary key
            _idTree.Update(new BTreeKey(0, nodeId), newKey.Serialize());

            // Update child counts
            UpdateChildCount(oldParentId, -1);
            UpdateChildCount(newParentId, 1);

            AutoCommit();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Recursively deletes a node and all its descendants.</summary>
    public void DeleteSubtree(ulong nodeId)
    {
        ThrowIfReadOnly();
        try
        {
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
                _idTree.Delete(new BTreeKey(0, rec.Id));
                IdCacheInvalidate(rec.Id);
            }

            // Delete the node itself
            FreeNodeData(nodeRecord);
            _tree.Delete(nodeKey);
            _idTree.Delete(new BTreeKey(0, nodeId));
            IdCacheInvalidate(nodeId);

            // Update parent's child count
            UpdateChildCount(parentId, -1);

            AutoCommit();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
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

    /// <summary>Begins an explicit transaction. Acquires the write lock.</summary>
    public void BeginTransaction()
    {
        ThrowIfReadOnly();
        AcquireWriteLockAndRefresh();
        try
        {
            _txn.Begin();
        }
        catch
        {
            ReleaseWriteLockIfHeld();
            throw;
        }
    }

    /// <summary>Commits the current transaction. Releases the write lock.</summary>
    public void CommitTransaction()
    {
        try
        {
            _txn.Commit();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Rolls back the current transaction. Releases the write lock.</summary>
    public void RollbackTransaction()
    {
        try
        {
            _txn.Rollback();
            IdCacheClear();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Auto-commits if no explicit transaction is active. Releases the write lock.</summary>
    private void AutoCommit()
    {
        if (!_txn.HasActiveTransaction)
        {
            try
            {
                CommitInternal();
            }
            finally
            {
                ReleaseWriteLockIfHeld();
            }
        }
    }

    /// <summary>
    /// Acquires the write lock and refreshes state from disk.
    /// Called when beginning an explicit transaction or any auto-commit write.
    /// </summary>
    private void AcquireWriteLockAndRefresh()
    {
        if (!_writeLockHeld)
        {
            _file.AcquireWriteLock(_lockTimeout);
            _writeLockHeld = true;
            try
            {
                _file.RefreshFileSize();
                RefreshFromDisk();
            }
            catch
            {
                _writeLockHeld = false;
                _file.ReleaseWriteLock();
                throw;
            }
        }
    }

    /// <summary>Releases the write lock if held.</summary>
    private void ReleaseWriteLockIfHeld()
    {
        if (_writeLockHeld)
        {
            _writeLockHeld = false;
            _file.ReleaseWriteLock();
        }
    }

    /// <summary>Persists current state to superblock (called by TransactionManager on commit).</summary>
    internal void CommitInternal()
    {
        var sb = _sbManager.Active;

        // Issue 5 fix: Free old buddy state block before allocating new one
        if (sb.BuddyRootAddress != 0 && _lastBuddyBlockOrder > 0)
            _allocator.Free((long)sb.BuddyRootAddress, _lastBuddyBlockOrder);

        // Issue 6 fix: Allocate block first, then serialize (so serialized state
        // reflects the allocation of its own storage block)
        int estimatedSize = _allocator.SerializedSize;
        int buddyOrder = FormatConstants.OrderForPayload(estimatedSize);
        long buddyAddr = _allocator.Allocate(buddyOrder);

        // Now serialize — this captures the state AFTER the buddy block allocation
        byte[] buddyState = _allocator.Serialize();

        // If the actual size exceeds the allocated block (unlikely but handle it)
        int actualOrder = FormatConstants.OrderForPayload(buddyState.Length);
        if (actualOrder > buddyOrder)
        {
            _allocator.Free(buddyAddr, buddyOrder);
            buddyOrder = actualOrder;
            buddyAddr = _allocator.Allocate(buddyOrder);
            buddyState = _allocator.Serialize(); // re-serialize with updated state
        }

        _file.WriteBlock(buddyAddr, buddyOrder, buddyState);
        _lastBuddyBlockOrder = buddyOrder;

        sb.BTreeRootAddress = (ulong)_tree.RootAddress;
        sb.IdIndexRootAddress = (ulong)_idTree.RootAddress;
        sb.BuddyRootAddress = (ulong)buddyAddr;
        sb.ContainerSize = (ulong)_file.FileSize;
        sb.NextNodeId = _nextNodeId;

        // SuperblockManager.Commit() calls _file.Flush() which flushes ALL pending
        // writes on this handle (Windows FlushFileBuffers semantics). No need for
        // a separate pre-flush — the single flush after superblock write ensures
        // both data blocks and superblock reach disk atomically.
        _sbManager.Commit(sb);
        _lastKnownGeneration = _sbManager.Active.Generation;

        // The allocator we just serialized includes any previously-pending B-tree frees
        // (replayed during RefreshFromDisk or still in memory from last commit).
        // Now that they're persisted, clear the pending list.
        _pendingBTreeFrees.Clear();

        // Free old B-tree nodes that were replaced by COW mutations.
        // Safe to free after commit since the superblock now points to the new trees.
        // Track them as pending in case a Refresh() reloads the allocator before next commit.
        foreach (long freedAddr in _tree.FreedBlocks)
        {
            int order = GetBlockOrderFromHeader(freedAddr);
            _allocator.Free(freedAddr, order);
            _pendingBTreeFrees.Add(freedAddr);
        }
        _tree.FreedBlocks.Clear();

        foreach (long freedAddr in _idTree.FreedBlocks)
        {
            int order = GetBlockOrderFromHeader(freedAddr);
            _allocator.Free(freedAddr, order);
            _pendingBTreeFrees.Add(freedAddr);
        }
        _idTree.FreedBlocks.Clear();
    }

    /// <summary>Restores B-tree roots (used by TransactionManager on rollback).</summary>
    internal void RestoreRootAddress(long address)
    {
        _tree = new BTree(_allocator, _file, address, _tree.Order);
    }

    /// <summary>Restores ID index tree root (used by TransactionManager on rollback).</summary>
    internal void RestoreIdTreeRootAddress(long address)
    {
        _idTree = new BTree(_allocator, _file, address, _idTree.Order);
    }

    /// <summary>Restores the next node ID counter (used by TransactionManager on rollback).</summary>
    internal void RestoreNextNodeId(ulong id) => _nextNodeId = id;

    /// <summary>Rebuilds the ID index from the primary tree (one-time migration). Commits the result.</summary>
    private void RebuildIdIndex()
    {
        foreach (var (key, value) in _tree.ScanAll())
        {
            var record = NodeRecord.Deserialize(value);
            _idTree.Insert(new BTreeKey(0, record.Id), key.Serialize());
        }
        CommitInternal();
    }

    /// <summary>Rebuilds the ID index in memory only (for read-only mode migration).</summary>
    private void RebuildIdIndexReadOnly()
    {
        // In read-only mode we can't persist, but we can build the tree in memory
        // using temporary allocations. The tree writes go to the file but since
        // it's read-only logically, we just build the mapping in a local dictionary
        // and override FindById to use it. However, since our BTree always writes to
        // disk, for truly read-only we fall back to the scan approach.
        // For simplicity: just rebuild in memory — the tree will allocate blocks but
        // since the file is opened read-only, this will throw. Instead, use a fallback.
        _idTreeFallback = new Dictionary<ulong, BTreeKey>();
        foreach (var (key, value) in _tree.ScanAll())
        {
            var record = NodeRecord.Deserialize(value);
            _idTreeFallback[record.Id] = key;
        }
    }

    // --- Metadata ---

    /// <summary>Sets a metadata key-value pair on an object.</summary>
    public void SetMetadata(ulong id, string key, string value)
    {
        ThrowIfReadOnly();
        try
        {
            var (treeKey, record) = FindById(id);
            if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
            IdCacheInvalidate(id); // Invalidate before mutation

            var metadata = LoadMetadata(record);
            metadata[key] = value;
            record.MetadataBlockAddress = SaveMetadata(metadata, record.MetadataBlockAddress);
            record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _tree.Update(treeKey, record.Serialize());
            AutoCommit();
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
    }

    /// <summary>Gets a metadata value by key, or null if not found.</summary>
    public string? GetMetadata(ulong id, string key)
    {
        RefreshIfStale();
        var (_, record) = FindById(id);
        if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");

        var metadata = LoadMetadata(record);
        return metadata.GetValueOrDefault(key);
    }

    /// <summary>Deletes a metadata key. Returns true if the key existed.</summary>
    public bool DeleteMetadata(ulong id, string key)
    {
        ThrowIfReadOnly();
        try
        {
            var (treeKey, record) = FindById(id);
            if (record == null) throw new ObjectNotFoundException($"Object {id} not found.");
            IdCacheInvalidate(id); // Invalidate before mutation

            var metadata = LoadMetadata(record);
            if (!metadata.Remove(key)) return false;

            record.MetadataBlockAddress = SaveMetadata(metadata, record.MetadataBlockAddress);
            record.Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _tree.Update(treeKey, record.Serialize());
            AutoCommit();
            return true;
        }
        finally
        {
            if (!_txn.HasActiveTransaction)
                ReleaseWriteLockIfHeld();
        }
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

        long addr = AllocateTracked(order);
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
        {
            try
            {
                SetDirtyFlag(false);
            }
            catch { /* best-effort */ }
        }
        ReleaseWriteLockIfHeld();
        _file.Dispose();
    }

    /// <summary>Sets or clears the dirty-open flag on the latest committed superblock.</summary>
    internal void SetDirtyFlag(bool dirty)
    {
        bool releaseLock = !_writeLockHeld;
        AcquireWriteLockAndRefresh();

        try
        {
            var sb = _sbManager.Active;
            if (dirty)
                sb.Flags |= Superblock.FlagDirtyOpen;
            else
                sb.Flags &= ~Superblock.FlagDirtyOpen;

            if (sb.Flags == _sbManager.Active.Flags)
                return;

            _sbManager.Commit(sb);
            _lastKnownGeneration = _sbManager.Active.Generation;
        }
        finally
        {
            if (releaseLock)
                ReleaseWriteLockIfHeld();
        }
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

    /// <summary>
    /// Allocates a block and tracks it in the active transaction for rollback.
    /// </summary>
    private long AllocateTracked(int order)
    {
        long addr = _allocator.Allocate(order);
        if (_txn.HasActiveTransaction)
            _txn.TrackNewBlock(addr, order);
        return addr;
    }

    /// <summary>
    /// Inserts a record into the primary tree, handling hash collisions.
    /// If the key already exists with a different name, uses a fallback key (parentId, nameHash ^ id).
    /// Returns the actual key used (may differ from input if collision occurred).
    /// </summary>
    private BTreeKey InsertWithCollisionHandling(BTreeKey key, NodeRecord record)
    {
        try
        {
            _tree.Insert(key, record.Serialize());
            return key;
        }
        catch (ObjectAlreadyExistsException)
        {
            // Check if it's a genuine duplicate (same name) or a hash collision
            var existingData = _tree.Get(key);
            if (existingData != null)
            {
                var existing = NodeRecord.Deserialize(existingData);
                if (existing.Name == record.Name && existing.ParentId == record.ParentId)
                    throw; // Genuine duplicate name under same parent
            }

            // Hash collision — use fallback key: (parentId, nameHash ^ id)
            var fallbackKey = new BTreeKey(record.ParentId, record.NameHash ^ record.Id);
            _tree.Insert(fallbackKey, record.Serialize());
            return fallbackKey;
        }
    }

    private (BTreeKey Key, NodeRecord? Record) FindById(ulong id)
    {
        // Check ID cache first
        if (_idCache.TryGetValue(id, out var cached))
            return (cached.Key, cached.Record);

        // Read-only migration fallback (in-memory dictionary)
        if (_idTreeFallback != null)
        {
            if (!_idTreeFallback.TryGetValue(id, out var primaryKey))
                return (default, null);
            var val = _tree.Get(primaryKey);
            if (val == null) return (default, null);
            var rec = NodeRecord.Deserialize(val);
            IdCachePut(id, primaryKey, rec);
            return (primaryKey, rec);
        }

        // Use secondary ID index: key=(0, id), value=serialized primary BTreeKey
        var idKey = new BTreeKey(0, id);
        var primaryKeyData = _idTree.Get(idKey);
        if (primaryKeyData == null)
            return (default, null);

        var pKey = BTreeKey.ReadFrom(primaryKeyData);
        var value = _tree.Get(pKey);
        if (value == null)
            return (default, null);

        var record = NodeRecord.Deserialize(value);
        IdCachePut(id, pKey, record);
        return (pKey, record);
    }

    private void IdCachePut(ulong id, BTreeKey key, NodeRecord record)
    {
        if (_idCache.Count >= IdCacheCapacity)
            _idCache.Clear(); // simple eviction: clear all when full
        _idCache[id] = (key, record);
    }

    private void IdCacheInvalidate(ulong id)
    {
        _idCache.Remove(id);
    }

    internal void IdCacheClear()
    {
        _idCache.Clear();
    }

    /// <summary>
    /// Promotes an inline-data object to extent-based storage.
    /// Allocates data blocks for the existing inline payload and clears inline state.
    /// </summary>
    private void PromoteInlineToExtents(NodeRecord record, BTreeKey key)
    {
        byte[] inlineData = record.InlineData!;
        record.InlineData = null;
        record.NodeTypeFlags = (byte)(record.NodeTypeFlags & ~NodeRecord.FlagInlineData);

        // Write inline data to extent blocks
        var extents = new ExtentList();
        int remaining = inlineData.Length;
        int dataOffset = 0;

        while (remaining > 0)
        {
            int order = FormatConstants.OrderForPayload(Math.Min(remaining, FormatConstants.PayloadSizeForOrder(FormatConstants.OrderCount - 1)));
            int capacity = FormatConstants.PayloadSizeForOrder(order);
            int toWrite = Math.Min(remaining, capacity);

            long blockAddr = AllocateTracked(order);
            _file.WriteBlock(blockAddr, order, inlineData.AsSpan(dataOffset, toWrite));
            extents.Extents.Add((blockAddr, order));

            dataOffset += toWrite;
            remaining -= toWrite;
        }

        record.ExtentListAddress = SaveExtentList(extents, record.ExtentListAddress);
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

        long addr = AllocateTracked(order);
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
        // Acquire write lock if not in an explicit transaction (auto-commit mode)
        // or if we're in a transaction that hasn't acquired it yet.
        AcquireWriteLockAndRefresh();
    }
}
