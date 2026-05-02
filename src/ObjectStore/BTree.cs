namespace ObjectStore;

/// <summary>
/// COW B+ tree with composite (parent_id, name_hash) keys.
/// All key-value pairs reside in leaf nodes only. Internal nodes hold separator keys.
/// Each mutation produces new nodes; old node addresses go into FreedBlocks.
/// 
/// Transaction optimization: when BeginBatchMode() is active, blocks allocated
/// by this tree are tracked. Subsequent mutations to the same block reuse it
/// in-place (no new allocation), dramatically reducing write amplification.
/// </summary>
public sealed class BTree
{
    private readonly BuddyAllocator _allocator;
    private readonly ContainerFile _file;
    private readonly int _order; // minimum degree t
    private readonly int _nodeBlockOrder;

    public long RootAddress { get; private set; }
    public List<long> FreedBlocks { get; } = new();
    public int Order => _order;

    /// <summary>Tracks block addresses allocated by this tree in the current batch/transaction, with their order.</summary>
    private Dictionary<long, int>? _batchOwnedBlocks;

    /// <summary>
    /// During batch mode, holds modified nodes that haven't been serialized to disk yet.
    /// Avoids redundant serialization when the same node is modified multiple times in a batch.
    /// </summary>
    private Dictionary<long, BTreeNode>? _dirtyNodes;

    private const int DefaultOrder = 32;
    private const int DefaultNodeBlockOrder = 8; // 16KB — historical default, now nodes are dynamically sized

    public BTree(BuddyAllocator allocator, ContainerFile file,
                 long rootAddress = 0, int order = DefaultOrder, int nodeBlockOrder = DefaultNodeBlockOrder)
    {
        _allocator = allocator;
        _file = file;
        _order = order;
        _nodeBlockOrder = nodeBlockOrder;
        RootAddress = rootAddress;
    }

    /// <summary>Enables batch mode: subsequent writes reuse blocks allocated in this batch.</summary>
    public void BeginBatchMode()
    {
        _batchOwnedBlocks = new Dictionary<long, int>();
        _dirtyNodes = new Dictionary<long, BTreeNode>();
    }

    /// <summary>Ends batch mode: flushes all dirty nodes to disk and clears tracking.</summary>
    public void EndBatchMode()
    {
        if (_dirtyNodes != null && _dirtyNodes.Count > 0)
            FlushDirtyNodes();
        _batchOwnedBlocks = null;
        _dirtyNodes = null;
    }

    /// <summary>
    /// Serializes and writes all dirty nodes. If a node outgrew its allocated block,
    /// allocates a new larger block and updates all parent references.
    /// </summary>
    private void FlushDirtyNodes()
    {
        if (_dirtyNodes == null || _batchOwnedBlocks == null) return;

        // We may need multiple passes if a node moves (parent references change).
        // Collect address relocations and fix up parent.Children entries.
        var relocations = new Dictionary<long, long>(); // old → new

        foreach (var (address, node) in _dirtyNodes)
        {
            byte[] data = node.Serialize();
            int newOrder = FormatConstants.OrderForPayload(data.Length);

            if (_batchOwnedBlocks.TryGetValue(address, out int oldOrder) && newOrder <= oldOrder)
            {
                // Fits in current block — write in place
                _file.WriteBlockOwned(address, oldOrder, data);
            }
            else
            {
                // Outgrew block — allocate new, free old
                if (_batchOwnedBlocks.TryGetValue(address, out int curOrder))
                {
                    _batchOwnedBlocks.Remove(address);
                    _allocator.Free(address, curOrder);
                }
                long newAddr = _allocator.Allocate(newOrder);
                _file.WriteBlockOwned(newAddr, newOrder, data);
                _batchOwnedBlocks[newAddr] = newOrder;
                node.Address = newAddr;
                relocations[address] = newAddr;
            }
        }

        // Fix up child pointers in dirty nodes if any children relocated
        if (relocations.Count > 0)
        {
            foreach (var (_, node) in _dirtyNodes)
            {
                if (node.IsLeaf) continue;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (relocations.TryGetValue(node.Children[i], out long newAddr))
                        node.Children[i] = newAddr;
                }
            }
            // Re-serialize relocated parents (they may have been already written with old refs)
            foreach (var (_, node) in _dirtyNodes)
            {
                if (node.IsLeaf || node.Address == 0) continue;
                bool hasRelocatedChild = false;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (relocations.ContainsValue(node.Children[i]))
                    { hasRelocatedChild = true; break; }
                }
                if (hasRelocatedChild)
                {
                    byte[] data = node.Serialize();
                    int order = FormatConstants.OrderForPayload(data.Length);
                    if (_batchOwnedBlocks.TryGetValue(node.Address, out int curOrd) && order <= curOrd)
                        _file.WriteBlockOwned(node.Address, curOrd, data);
                }
            }
            // Update root if relocated
            if (relocations.TryGetValue(RootAddress, out long newRoot))
                RootAddress = newRoot;
        }
    }

    public byte[]? Get(BTreeKey key)
    {
        if (RootAddress == 0) return null;
        return SearchLeaf(RootAddress, key);
    }

    public bool TryGet(BTreeKey key, out byte[]? value)
    {
        value = Get(key);
        return value != null;
    }

    public void Insert(BTreeKey key, byte[] value)
    {
        if (RootAddress == 0)
        {
            var root = new BTreeNode { NodeType = BTreeNode.TypeLeaf };
            root.Keys.Add(key);
            root.Values.Add(value);
            RootAddress = WriteNode(root);
            return;
        }

        // Check if root is full
        var rootNode = ReadNode(RootAddress);
        if (rootNode.KeyCount >= 2 * _order - 1)
        {
            // Split root — SplitChild already adds old root address to FreedBlocks
            var newRoot = new BTreeNode { NodeType = BTreeNode.TypeInternal };
            newRoot.Children.Add(RootAddress);
            SplitChild(newRoot, 0);
            InsertNonFull(newRoot, key, value);
            RootAddress = WriteNode(newRoot);
        }
        else
        {
            var newRoot = InsertNonFull(rootNode, key, value);
            RootAddress = WriteNodeReplace(rootNode, RootAddress);
        }
    }

    public bool Update(BTreeKey key, byte[] newValue)
    {
        if (RootAddress == 0) return false;
        long newRoot = UpdateLeaf(RootAddress, key, newValue, out bool found);
        if (found)
        {
            RootAddress = newRoot;
        }
        return found;
    }

    public bool Delete(BTreeKey key)
    {
        if (RootAddress == 0) return false;

        var rootNode = ReadNode(RootAddress);
        bool found = DeleteKey(rootNode, key);
        if (!found) return false;

        if (rootNode.KeyCount == 0 && !rootNode.IsLeaf)
        {
            // Root collapsed — free the old root block
            if (_batchOwnedBlocks != null && _batchOwnedBlocks.ContainsKey(RootAddress))
                _batchOwnedBlocks.Remove(RootAddress);
            FreedBlocks.Add(RootAddress);
            RootAddress = rootNode.Children[0];
        }
        else if (rootNode.KeyCount == 0 && rootNode.IsLeaf)
        {
            if (_batchOwnedBlocks != null && _batchOwnedBlocks.ContainsKey(RootAddress))
                _batchOwnedBlocks.Remove(RootAddress);
            FreedBlocks.Add(RootAddress);
            RootAddress = 0;
        }
        else
        {
            RootAddress = WriteNodeReplace(rootNode, RootAddress);
        }
        return true;
    }

    public IEnumerable<(BTreeKey Key, byte[] Value)> RangeScan(ulong parentId)
    {
        if (RootAddress == 0) yield break;
        var startKey = new BTreeKey(parentId, 0);
        var endKey = new BTreeKey(parentId, ulong.MaxValue);
        foreach (var kv in ScanRange(RootAddress, startKey, endKey))
            yield return kv;
    }

    public IEnumerable<(BTreeKey Key, byte[] Value)> ScanAll()
    {
        if (RootAddress == 0) yield break;
        foreach (var kv in ScanLeaves(RootAddress))
            yield return kv;
    }

    // ---- Private ----

    private BTreeNode ReadNode(long address)
    {
        if (_dirtyNodes != null && _dirtyNodes.TryGetValue(address, out var dirty))
            return dirty;
        byte[] data = _file.ReadBlockAutoPayload(address);
        return BTreeNode.Deserialize(data);
    }

    private long WriteNode(BTreeNode node)
    {
        byte[] data = node.Serialize();
        int order = FormatConstants.OrderForPayload(data.Length);
        long address = _allocator.Allocate(order);
        _file.WriteBlockOwned(address, order, data);
        node.Address = address;
        _batchOwnedBlocks?.TryAdd(address, order);
        // Track in dirty nodes so ReadNode finds it without deserialization
        _dirtyNodes?.TryAdd(address, node);
        return address;
    }

    /// <summary>
    /// Writes a modified node, reusing its old address if it was allocated in the current batch.
    /// In batch mode with dirty-node tracking, defers serialization entirely.
    /// Otherwise performs normal COW (allocate new, track old for freeing).
    /// </summary>
    private long WriteNodeReplace(BTreeNode node, long oldAddress)
    {
        if (_batchOwnedBlocks != null && _batchOwnedBlocks.TryGetValue(oldAddress, out int oldOrder))
        {
            // Batch-owned: defer serialization — just mark dirty
            if (_dirtyNodes != null)
            {
                node.Address = oldAddress;
                _dirtyNodes[oldAddress] = node;
                return oldAddress;
            }

            byte[] data = node.Serialize();
            int newOrder = FormatConstants.OrderForPayload(data.Length);

            if (newOrder <= oldOrder)
            {
                // Still fits — reuse in place
                _file.WriteBlockOwned(oldAddress, oldOrder, data);
                node.Address = oldAddress;
                return oldAddress;
            }
            else
            {
                // Outgrew old block — free old, allocate new
                _batchOwnedBlocks.Remove(oldAddress);
                _allocator.Free(oldAddress, oldOrder);
                long address = _allocator.Allocate(newOrder);
                _file.WriteBlockOwned(address, newOrder, data);
                node.Address = address;
                _batchOwnedBlocks[address] = newOrder;
                return address;
            }
        }
        else
        {
            // Normal COW: free old, allocate new
            FreedBlocks.Add(oldAddress);
            return WriteNode(node);
        }
    }

    private byte[]? SearchLeaf(long address, BTreeKey key)
    {
        // If the node is dirty (modified in this batch), search it in-memory
        if (_dirtyNodes != null && _dirtyNodes.TryGetValue(address, out var dirtyNode))
            return SearchNodeInMemory(dirtyNode, key);
        byte[] data = _file.ReadBlockAutoPayload(address);
        return SearchLeafDirect(data, key);
    }

    /// <summary>Searches for a key in an in-memory BTreeNode (used for dirty nodes).</summary>
    private byte[]? SearchNodeInMemory(BTreeNode node, BTreeKey key)
    {
        if (node.IsLeaf)
        {
            int i = FindKeyIndex(node, key);
            if (i < node.KeyCount && node.Keys[i] == key)
                return node.Values[i];
            return null;
        }
        else
        {
            int childIdx = FindChildIndex(node, key);
            return SearchLeaf(node.Children[childIdx], key);
        }
    }

    /// <summary>
    /// Searches for a key directly in serialized node data without full deserialization.
    /// Binary-searches fixed-size keys, then extracts only the matching value.
    /// </summary>
    private byte[]? SearchLeafDirect(ReadOnlySpan<byte> data, BTreeKey key)
    {
        byte nodeType = data[0];
        ushort keyCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        // offset 3 = reserved
        int keysStart = 4;

        if (nodeType == BTreeNode.TypeLeaf)
        {
            // Binary search over fixed-size keys (16 bytes each)
            int lo = 0, hi = keyCount - 1;
            int foundIdx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var midKey = BTreeKey.ReadFrom(data[(keysStart + mid * BTreeKey.Size)..]);
                int cmp = midKey.CompareTo(key);
                if (cmp == 0) { foundIdx = mid; break; }
                else if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }

            if (foundIdx < 0) return null;

            // Skip to the value at foundIdx: values are length-prefixed (u16 + data)
            int valuesStart = keysStart + keyCount * BTreeKey.Size;
            int offset = valuesStart;
            for (int i = 0; i < foundIdx; i++)
            {
                ushort len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                offset += 2 + len;
            }
            ushort valueLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            return data.Slice(offset + 2, valueLen).ToArray();
        }
        else
        {
            // Internal node: find child index via binary search, descend
            // Original logic: first i where key < keys[i] (key >= keys[i] means keep going right)
            int childIdx = keyCount; // default: rightmost child
            int lo = 0, hi = keyCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var midKey = BTreeKey.ReadFrom(data[(keysStart + mid * BTreeKey.Size)..]);
                int cmp = key.CompareTo(midKey);
                if (cmp < 0) { childIdx = mid; hi = mid - 1; }
                else lo = mid + 1;
            }

            // Children start after keys
            int childrenStart = keysStart + keyCount * BTreeKey.Size;
            long childAddr = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                data[(childrenStart + childIdx * 8)..]);

            // Child might be dirty — use SearchLeaf which checks dirty nodes
            return SearchLeaf(childAddr, key);
        }
    }

    private BTreeNode InsertNonFull(BTreeNode node, BTreeKey key, byte[] value)
    {
        if (node.IsLeaf)
        {
            int i = FindKeyIndex(node, key);
            if (i < node.KeyCount && node.Keys[i] == key)
                throw new ObjectAlreadyExistsException($"Key ({key.ParentId}, {key.NameHash}) already exists.");
            node.Keys.Insert(i, key);
            node.Values.Insert(i, value);
            return node;
        }

        // Internal node
        int childIdx = FindChildIndex(node, key);
        var child = ReadNode(node.Children[childIdx]);

        if (child.KeyCount >= 2 * _order - 1)
        {
            SplitChild(node, childIdx);
            // After split, determine which child to use
            if (key > node.Keys[childIdx])
                childIdx++;
            else if (key == node.Keys[childIdx])
            {
                // B+ tree: separator can equal a leaf key. Go right.
                childIdx++;
            }
            child = ReadNode(node.Children[childIdx]);
        }

        InsertNonFull(child, key, value);
        node.Children[childIdx] = WriteNodeReplace(child, node.Children[childIdx]);
        return node;
    }

    private void SplitChild(BTreeNode parent, int childIndex)
    {
        long oldAddr = parent.Children[childIndex];
        var fullChild = ReadNode(oldAddr);
        var newRight = new BTreeNode { NodeType = fullChild.NodeType };

        int mid = _order - 1; // index of median

        if (fullChild.IsLeaf)
        {
            // B+ tree leaf split: right gets keys[mid..], left keeps keys[0..mid-1]
            for (int j = mid; j < fullChild.KeyCount; j++)
            {
                newRight.Keys.Add(fullChild.Keys[j]);
                newRight.Values.Add(fullChild.Values[j]);
            }
            var separator = fullChild.Keys[mid];
            fullChild.Keys.RemoveRange(mid, fullChild.Keys.Count - mid);
            fullChild.Values.RemoveRange(mid, fullChild.Values.Count - mid);

            // Reuse old address for left child if batch-owned
            long leftAddr = WriteNodeReplace(fullChild, oldAddr);
            long rightAddr = WriteNode(newRight);

            parent.Keys.Insert(childIndex, separator);
            parent.Children[childIndex] = leftAddr;
            parent.Children.Insert(childIndex + 1, rightAddr);
        }
        else
        {
            // Internal node split: median key is promoted, not duplicated
            var separator = fullChild.Keys[mid];

            for (int j = mid + 1; j < fullChild.KeyCount; j++)
                newRight.Keys.Add(fullChild.Keys[j]);
            for (int j = mid + 1; j < fullChild.Children.Count; j++)
                newRight.Children.Add(fullChild.Children[j]);

            fullChild.Keys.RemoveRange(mid, fullChild.Keys.Count - mid);
            fullChild.Children.RemoveRange(mid + 1, fullChild.Children.Count - (mid + 1));

            long leftAddr = WriteNodeReplace(fullChild, oldAddr);
            long rightAddr = WriteNode(newRight);

            parent.Keys.Insert(childIndex, separator);
            parent.Children[childIndex] = leftAddr;
            parent.Children.Insert(childIndex + 1, rightAddr);
        }
    }

    private long UpdateLeaf(long address, BTreeKey key, byte[] newValue, out bool found)
    {
        var node = ReadNode(address);

        if (node.IsLeaf)
        {
            int i = FindKeyIndex(node, key);
            if (i < node.KeyCount && node.Keys[i] == key)
            {
                node.Values[i] = newValue;
                found = true;
                return WriteNodeReplace(node, address);
            }
            found = false;
            return address;
        }

        int childIdx = FindChildIndex(node, key);
        long newChildAddr = UpdateLeaf(node.Children[childIdx], key, newValue, out found);
        if (found)
        {
            node.Children[childIdx] = newChildAddr;
            return WriteNodeReplace(node, address);
        }
        return address;
    }

    private bool DeleteKey(BTreeNode node, BTreeKey key)
    {
        if (node.IsLeaf)
        {
            int i = FindKeyIndex(node, key);
            if (i < node.KeyCount && node.Keys[i] == key)
            {
                node.Keys.RemoveAt(i);
                node.Values.RemoveAt(i);
                return true;
            }
            return false;
        }

        // Internal node: find the child that may contain the key
        int childIdx = FindChildIndex(node, key);
        var child = ReadNode(node.Children[childIdx]);

        if (child.KeyCount <= _order - 1)
        {
            FillChild(node, childIdx);
            // After fill, structure may have changed, re-determine child
            childIdx = FindChildIndex(node, key);
            if (childIdx >= node.Children.Count)
                childIdx = node.Children.Count - 1;
            child = ReadNode(node.Children[childIdx]);
        }

        bool found = DeleteKey(child, key);
        if (found)
        {
            node.Children[childIdx] = WriteNodeReplace(child, node.Children[childIdx]);
            UpdateSeparators(node);
        }
        return found;
    }

    private void FillChild(BTreeNode parent, int childIdx)
    {
        // Try borrowing from left sibling
        if (childIdx > 0)
        {
            var leftSibling = ReadNode(parent.Children[childIdx - 1]);
            if (leftSibling.KeyCount > _order - 1)
            {
                BorrowFromLeft(parent, childIdx);
                return;
            }
        }

        // Try borrowing from right sibling
        if (childIdx < parent.Children.Count - 1)
        {
            var rightSibling = ReadNode(parent.Children[childIdx + 1]);
            if (rightSibling.KeyCount > _order - 1)
            {
                BorrowFromRight(parent, childIdx);
                return;
            }
        }

        // Merge
        if (childIdx < parent.Children.Count - 1)
            Merge(parent, childIdx);
        else
            Merge(parent, childIdx - 1);
    }

    private void BorrowFromLeft(BTreeNode parent, int childIdx)
    {
        var child = ReadNode(parent.Children[childIdx]);
        var leftSibling = ReadNode(parent.Children[childIdx - 1]);

        if (child.IsLeaf)
        {
            // Move last key-value from left sibling to front of child
            child.Keys.Insert(0, leftSibling.Keys[^1]);
            child.Values.Insert(0, leftSibling.Values[^1]);
            leftSibling.Keys.RemoveAt(leftSibling.Keys.Count - 1);
            leftSibling.Values.RemoveAt(leftSibling.Values.Count - 1);
            // Update separator
            parent.Keys[childIdx - 1] = child.Keys[0];
        }
        else
        {
            // Move separator down, last key of left up
            child.Keys.Insert(0, parent.Keys[childIdx - 1]);
            child.Children.Insert(0, leftSibling.Children[^1]);
            parent.Keys[childIdx - 1] = leftSibling.Keys[^1];
            leftSibling.Keys.RemoveAt(leftSibling.Keys.Count - 1);
            leftSibling.Children.RemoveAt(leftSibling.Children.Count - 1);
        }

        parent.Children[childIdx] = WriteNodeReplace(child, parent.Children[childIdx]);
        parent.Children[childIdx - 1] = WriteNodeReplace(leftSibling, parent.Children[childIdx - 1]);
    }

    private void BorrowFromRight(BTreeNode parent, int childIdx)
    {
        var child = ReadNode(parent.Children[childIdx]);
        var rightSibling = ReadNode(parent.Children[childIdx + 1]);

        if (child.IsLeaf)
        {
            child.Keys.Add(rightSibling.Keys[0]);
            child.Values.Add(rightSibling.Values[0]);
            rightSibling.Keys.RemoveAt(0);
            rightSibling.Values.RemoveAt(0);
            parent.Keys[childIdx] = rightSibling.Keys[0];
        }
        else
        {
            child.Keys.Add(parent.Keys[childIdx]);
            child.Children.Add(rightSibling.Children[0]);
            parent.Keys[childIdx] = rightSibling.Keys[0];
            rightSibling.Keys.RemoveAt(0);
            rightSibling.Children.RemoveAt(0);
        }

        parent.Children[childIdx] = WriteNodeReplace(child, parent.Children[childIdx]);
        parent.Children[childIdx + 1] = WriteNodeReplace(rightSibling, parent.Children[childIdx + 1]);
    }

    private void Merge(BTreeNode parent, int leftIdx)
    {
        var leftChild = ReadNode(parent.Children[leftIdx]);
        var rightChild = ReadNode(parent.Children[leftIdx + 1]);

        if (leftChild.IsLeaf)
        {
            leftChild.Keys.AddRange(rightChild.Keys);
            leftChild.Values.AddRange(rightChild.Values);
        }
        else
        {
            leftChild.Keys.Add(parent.Keys[leftIdx]);
            leftChild.Keys.AddRange(rightChild.Keys);
            leftChild.Children.AddRange(rightChild.Children);
        }

        parent.Keys.RemoveAt(leftIdx);
        // Right child is consumed by merge — free or remove from owned set
        long rightAddr = parent.Children[leftIdx + 1];
        if (_batchOwnedBlocks != null && _batchOwnedBlocks.ContainsKey(rightAddr))
            _batchOwnedBlocks.Remove(rightAddr);
        FreedBlocks.Add(rightAddr);
        parent.Children.RemoveAt(leftIdx + 1);
        // Left child is rewritten (reuse if batch-owned)
        parent.Children[leftIdx] = WriteNodeReplace(leftChild, parent.Children[leftIdx]);
    }

    private void UpdateSeparators(BTreeNode internalNode)
    {
        // Each separator key[i] should equal the minimum key in children[i+1]
        for (int i = 0; i < internalNode.Keys.Count; i++)
        {
            if (i + 1 < internalNode.Children.Count)
            {
                var rightChild = ReadNode(internalNode.Children[i + 1]);
                var minKey = GetMinKey(rightChild);
                if (minKey != null && internalNode.Keys[i] != minKey.Value)
                    internalNode.Keys[i] = minKey.Value;
            }
        }
    }

    private BTreeKey? GetMinKey(BTreeNode node)
    {
        while (!node.IsLeaf)
        {
            if (node.Children.Count == 0) return null;
            node = ReadNode(node.Children[0]);
        }
        return node.KeyCount > 0 ? node.Keys[0] : null;
    }

    private IEnumerable<(BTreeKey Key, byte[] Value)> ScanRange(long address, BTreeKey startKey, BTreeKey endKey)
    {
        var node = ReadNode(address);
        if (node.IsLeaf)
        {
            for (int i = 0; i < node.KeyCount; i++)
            {
                if (node.Keys[i] > endKey) yield break;
                if (node.Keys[i] >= startKey)
                    yield return (node.Keys[i], node.Values[i]);
            }
        }
        else
        {
            int startChild = 0;
            for (int i = 0; i < node.KeyCount; i++)
            {
                if (node.Keys[i] <= startKey)
                    startChild = i + 1;
                else
                    break;
            }

            for (int i = startChild; i < node.Children.Count; i++)
            {
                foreach (var kv in ScanRange(node.Children[i], startKey, endKey))
                    yield return kv;

                // If we've gone past the separator that exceeds endKey, stop
                if (i < node.KeyCount && node.Keys[i] > endKey)
                    yield break;
            }
        }
    }

    private IEnumerable<(BTreeKey Key, byte[] Value)> ScanLeaves(long address)
    {
        var node = ReadNode(address);
        if (node.IsLeaf)
        {
            for (int i = 0; i < node.KeyCount; i++)
                yield return (node.Keys[i], node.Values[i]);
        }
        else
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                foreach (var kv in ScanLeaves(node.Children[i]))
                    yield return kv;
            }
        }
    }

    /// <summary>
    /// For an internal node, find the child index to descend into for a given key.
    /// Uses the B+ tree rule: key &lt; separator[i] → go to children[i].
    /// </summary>
    private static int FindChildIndex(BTreeNode node, BTreeKey key)
    {
        int i = 0;
        while (i < node.KeyCount && key >= node.Keys[i])
            i++;
        return i;
    }

    /// <summary>Binary search for the position of key in a leaf (first index where keys[i] >= key).</summary>
    private static int FindKeyIndex(BTreeNode node, BTreeKey target)
    {
        int lo = 0, hi = node.KeyCount;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (node.Keys[mid] < target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
