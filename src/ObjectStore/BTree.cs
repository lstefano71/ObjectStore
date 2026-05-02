namespace ObjectStore;

/// <summary>
/// COW B+ tree with composite (parent_id, name_hash) keys.
/// All key-value pairs reside in leaf nodes only. Internal nodes hold separator keys.
/// Each mutation produces new nodes; old node addresses go into FreedBlocks.
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

    private const int DefaultOrder = 32;
    private const int DefaultNodeBlockOrder = 8; // 16KB — holds ~147 entries, well above split threshold of 63

    public BTree(BuddyAllocator allocator, ContainerFile file,
                 long rootAddress = 0, int order = DefaultOrder, int nodeBlockOrder = DefaultNodeBlockOrder)
    {
        _allocator = allocator;
        _file = file;
        _order = order;
        _nodeBlockOrder = nodeBlockOrder;
        RootAddress = rootAddress;
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
            // Split root
            var newRoot = new BTreeNode { NodeType = BTreeNode.TypeInternal };
            newRoot.Children.Add(RootAddress);
            SplitChild(newRoot, 0);
            InsertNonFull(newRoot, key, value);
            FreedBlocks.Add(RootAddress);
            RootAddress = WriteNode(newRoot);
        }
        else
        {
            var newRoot = InsertNonFull(rootNode, key, value);
            FreedBlocks.Add(RootAddress);
            RootAddress = WriteNode(rootNode);
        }
    }

    public bool Update(BTreeKey key, byte[] newValue)
    {
        if (RootAddress == 0) return false;
        long newRoot = UpdateLeaf(RootAddress, key, newValue, out bool found);
        if (found)
        {
            FreedBlocks.Add(RootAddress);
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

        FreedBlocks.Add(RootAddress);

        if (rootNode.KeyCount == 0 && !rootNode.IsLeaf)
        {
            RootAddress = rootNode.Children[0];
        }
        else if (rootNode.KeyCount == 0 && rootNode.IsLeaf)
        {
            RootAddress = 0;
        }
        else
        {
            RootAddress = WriteNode(rootNode);
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
        byte[] data = _file.ReadBlock(address, _nodeBlockOrder);
        return BTreeNode.Deserialize(data);
    }

    private long WriteNode(BTreeNode node)
    {
        byte[] data = node.Serialize();
        long address = _allocator.Allocate(_nodeBlockOrder);
        _file.WriteBlock(address, _nodeBlockOrder, data);
        node.Address = address;
        return address;
    }

    private byte[]? SearchLeaf(long address, BTreeKey key)
    {
        var node = ReadNode(address);

        if (node.IsLeaf)
        {
            int i = FindKeyIndex(node, key);
            if (i < node.KeyCount && node.Keys[i] == key)
                return node.Values[i];
            return null;
        }

        // Internal: find child to descend into
        int idx = FindChildIndex(node, key);
        return SearchLeaf(node.Children[idx], key);
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
                // Check: in a B+ tree, separator can equal a leaf key. Go right.
                childIdx++;
            }
            child = ReadNode(node.Children[childIdx]);
        }

        InsertNonFull(child, key, value);
        FreedBlocks.Add(node.Children[childIdx]);
        node.Children[childIdx] = WriteNode(child);
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
            // Separator promoted to parent = right's first key (keys[mid])
            for (int j = mid; j < fullChild.KeyCount; j++)
            {
                newRight.Keys.Add(fullChild.Keys[j]);
                newRight.Values.Add(fullChild.Values[j]);
            }
            var separator = fullChild.Keys[mid];
            fullChild.Keys.RemoveRange(mid, fullChild.Keys.Count - mid);
            fullChild.Values.RemoveRange(mid, fullChild.Values.Count - mid);

            FreedBlocks.Add(oldAddr);
            long leftAddr = WriteNode(fullChild);
            long rightAddr = WriteNode(newRight);

            parent.Keys.Insert(childIndex, separator);
            parent.Children[childIndex] = leftAddr;
            parent.Children.Insert(childIndex + 1, rightAddr);
        }
        else
        {
            // Internal node split: median key is promoted, not duplicated
            // Left: keys[0..mid-1], children[0..mid]
            // Right: keys[mid+1..], children[mid+1..]
            var separator = fullChild.Keys[mid];

            for (int j = mid + 1; j < fullChild.KeyCount; j++)
                newRight.Keys.Add(fullChild.Keys[j]);
            for (int j = mid + 1; j < fullChild.Children.Count; j++)
                newRight.Children.Add(fullChild.Children[j]);

            fullChild.Keys.RemoveRange(mid, fullChild.Keys.Count - mid);
            fullChild.Children.RemoveRange(mid + 1, fullChild.Children.Count - (mid + 1));

            FreedBlocks.Add(oldAddr);
            long leftAddr = WriteNode(fullChild);
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
                return WriteNode(node);
            }
            found = false;
            return address;
        }

        int childIdx = FindChildIndex(node, key);
        long newChildAddr = UpdateLeaf(node.Children[childIdx], key, newValue, out found);
        if (found)
        {
            FreedBlocks.Add(node.Children[childIdx]);
            node.Children[childIdx] = newChildAddr;
            return WriteNode(node);
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
            FreedBlocks.Add(node.Children[childIdx]);
            node.Children[childIdx] = WriteNode(child);

            // Update separator if needed: separator should be >= all keys in left subtree
            // and < all keys in right subtree. After deletion in leaf, check separators.
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

        FreedBlocks.Add(parent.Children[childIdx]);
        FreedBlocks.Add(parent.Children[childIdx - 1]);
        parent.Children[childIdx] = WriteNode(child);
        parent.Children[childIdx - 1] = WriteNode(leftSibling);
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

        FreedBlocks.Add(parent.Children[childIdx]);
        FreedBlocks.Add(parent.Children[childIdx + 1]);
        parent.Children[childIdx] = WriteNode(child);
        parent.Children[childIdx + 1] = WriteNode(rightSibling);
    }

    private void Merge(BTreeNode parent, int leftIdx)
    {
        var leftChild = ReadNode(parent.Children[leftIdx]);
        var rightChild = ReadNode(parent.Children[leftIdx + 1]);

        if (leftChild.IsLeaf)
        {
            // Merge right into left (no separator in leaf merge)
            leftChild.Keys.AddRange(rightChild.Keys);
            leftChild.Values.AddRange(rightChild.Values);
        }
        else
        {
            // Pull separator down, then merge
            leftChild.Keys.Add(parent.Keys[leftIdx]);
            leftChild.Keys.AddRange(rightChild.Keys);
            leftChild.Children.AddRange(rightChild.Children);
        }

        parent.Keys.RemoveAt(leftIdx);
        FreedBlocks.Add(parent.Children[leftIdx]);
        FreedBlocks.Add(parent.Children[leftIdx + 1]);
        parent.Children.RemoveAt(leftIdx + 1);
        parent.Children[leftIdx] = WriteNode(leftChild);
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
