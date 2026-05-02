namespace ObjectStore;

/// <summary>
/// Resolves slash-separated paths to node IDs by walking the B-tree.
/// Path format: "/name1/name2/name3" where each component is resolved
/// by scanning children of the current parent.
/// </summary>
public sealed class PathResolver
{
    private readonly ObjectEngine _engine;

    public PathResolver(ObjectEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Resolves a path string to a node ID.
    /// Returns null if any component along the path doesn't exist.
    /// The root node (ID=1) corresponds to "/".
    /// </summary>
    public ulong? Resolve(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return 1; // root

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        ulong currentId = 1; // start at root

        foreach (var part in parts)
        {
            var child = FindChildByName(currentId, part);
            if (child == null) return null;
            currentId = child.Id;
        }

        return currentId;
    }

    /// <summary>
    /// Finds a child node by name under a given parent.
    /// </summary>
    public NodeRecord? FindChildByName(ulong parentId, string name)
    {
        ulong nameHash = FnvHash.ComputeString(name);
        var key = new BTreeKey(parentId, nameHash);

        // Try exact key lookup first
        var value = _engine.Tree.Get(key);
        if (value != null)
        {
            var record = NodeRecord.Deserialize(value);
            if (record.Name == name && !record.IsDeleted)
                return record;
        }

        // Handle hash collisions: scan all children of this parent
        foreach (var (k, v) in _engine.Tree.RangeScan(parentId))
        {
            var record = NodeRecord.Deserialize(v);
            if (record.Name == name && !record.IsDeleted)
                return record;
        }

        return null;
    }

    /// <summary>
    /// Lists all direct children of a parent node.
    /// </summary>
    public IEnumerable<NodeRecord> ListChildren(ulong parentId)
    {
        foreach (var (_, v) in _engine.Tree.RangeScan(parentId))
        {
            var record = NodeRecord.Deserialize(v);
            if (!record.IsDeleted)
                yield return record;
        }
    }

    /// <summary>
    /// Recursively enumerates all descendants of a node (DFS).
    /// </summary>
    public IEnumerable<NodeRecord> EnumerateDescendants(ulong nodeId)
    {
        foreach (var child in ListChildren(nodeId))
        {
            yield return child;
            if (child.HasChildren)
            {
                foreach (var desc in EnumerateDescendants(child.Id))
                    yield return desc;
            }
        }
    }
}
