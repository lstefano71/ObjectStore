namespace ObjectStore.Tests;

/// <summary>
/// Tests for node hierarchy: path resolution, child creation, move, delete subtree.
/// </summary>
public class HierarchyTests : IDisposable
{
    private readonly string _path;
    private readonly ObjectEngine _engine;

    public HierarchyTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_hier_{Guid.NewGuid():N}.dat");
        _engine = ObjectEngine.Create(_path);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void CreateChild_UnderRoot()
    {
        var id = _engine.CreateChild(1, "folder1", isContainer: true);
        Assert.True(id > 1);

        var record = _engine.GetInfo(id);
        Assert.NotNull(record);
        Assert.Equal("folder1", record.Name);
        Assert.Equal((ulong)1, record.ParentId);
        Assert.True(record.HasChildren || record.NodeTypeFlags != 0);
    }

    [Fact]
    public void CreateChild_NestedHierarchy()
    {
        var folderId = _engine.CreateChild(1, "parent", isContainer: true);
        var childId = _engine.CreateChild(folderId, "child", isContainer: false);
        var grandchildId = _engine.CreateChild(childId, "grandchild", isContainer: false);

        // Verify path resolution
        Assert.Equal(folderId, _engine.ResolvePath("/parent"));
        Assert.Equal(childId, _engine.ResolvePath("/parent/child"));
        Assert.Equal(grandchildId, _engine.ResolvePath("/parent/child/grandchild"));
    }

    [Fact]
    public void ResolvePath_Root()
    {
        Assert.Equal((ulong)1, _engine.ResolvePath("/"));
        Assert.Equal((ulong)1, _engine.ResolvePath(""));
    }

    [Fact]
    public void ResolvePath_NotFound()
    {
        Assert.Null(_engine.ResolvePath("/nonexistent"));
        Assert.Null(_engine.ResolvePath("/a/b/c"));
    }

    [Fact]
    public void ListChildren()
    {
        _engine.CreateChild(1, "a");
        _engine.CreateChild(1, "b");
        _engine.CreateChild(1, "c");

        var children = _engine.ListChildren(1).ToList();
        Assert.Equal(3, children.Count);
        Assert.Contains(children, c => c.Name == "a");
        Assert.Contains(children, c => c.Name == "b");
        Assert.Contains(children, c => c.Name == "c");
    }

    [Fact]
    public void MoveNode_Basic()
    {
        var folder1 = _engine.CreateChild(1, "folder1", isContainer: true);
        var folder2 = _engine.CreateChild(1, "folder2", isContainer: true);
        var fileId = _engine.CreateChild(folder1, "myfile");

        // Verify original path
        Assert.Equal(fileId, _engine.ResolvePath("/folder1/myfile"));

        // Move to folder2
        _engine.MoveNode(fileId, folder2);

        // Should be at new location
        Assert.Equal(fileId, _engine.ResolvePath("/folder2/myfile"));
        Assert.Null(_engine.ResolvePath("/folder1/myfile"));
    }

    [Fact]
    public void MoveNode_WithRename()
    {
        var folderId = _engine.CreateChild(1, "folder", isContainer: true);
        var fileId = _engine.CreateChild(folderId, "original");

        _engine.MoveNode(fileId, 1, "renamed");

        Assert.Equal(fileId, _engine.ResolvePath("/renamed"));
        Assert.Null(_engine.ResolvePath("/folder/original"));
    }

    [Fact]
    public void MoveNode_CycleDetection()
    {
        var parentId = _engine.CreateChild(1, "parent", isContainer: true);
        var childId = _engine.CreateChild(parentId, "child", isContainer: true);

        // Try to move parent under its own child — should throw
        Assert.Throws<InvalidOperationException>(() => _engine.MoveNode(parentId, childId));
    }

    [Fact]
    public void DeleteSubtree()
    {
        var folderId = _engine.CreateChild(1, "folder", isContainer: true);
        var child1 = _engine.CreateChild(folderId, "child1");
        var child2 = _engine.CreateChild(folderId, "child2");

        // Add data to a child
        _engine.Append(child1, "data"u8);

        // Delete entire subtree
        _engine.DeleteSubtree(folderId);

        Assert.Null(_engine.ResolvePath("/folder"));
        Assert.False(_engine.Exists(folderId));
        Assert.False(_engine.Exists(child1));
        Assert.False(_engine.Exists(child2));
    }

    [Fact]
    public void DeleteSubtree_DeepHierarchy()
    {
        // Create a 5-level deep tree
        ulong current = 1;
        var ids = new List<ulong>();
        for (int i = 0; i < 5; i++)
        {
            current = _engine.CreateChild(current, $"level{i}", isContainer: true);
            ids.Add(current);
        }

        // Delete from level 1 (should delete levels 1-4)
        _engine.DeleteSubtree(ids[0]);

        for (int i = 0; i < 5; i++)
            Assert.False(_engine.Exists(ids[i]));
    }

    [Fact]
    public void ChildCount_UpdatedOnCreateAndDelete()
    {
        var folderId = _engine.CreateChild(1, "counted", isContainer: true);
        _engine.CreateChild(folderId, "a");
        _engine.CreateChild(folderId, "b");

        var info = _engine.GetInfo(folderId);
        Assert.Equal((uint)2, info!.ChildCount);

        // Delete subtree of one child
        var childA = _engine.ResolvePath("/counted/a")!.Value;
        _engine.DeleteSubtree(childA);

        info = _engine.GetInfo(folderId);
        Assert.Equal((uint)1, info!.ChildCount);
    }

    [Fact]
    public void Hierarchy_PersistsAcrossReopen()
    {
        var folderId = _engine.CreateChild(1, "persist-folder", isContainer: true);
        var fileId = _engine.CreateChild(folderId, "persist-file");
        _engine.Append(fileId, "persistent"u8);
        _engine.Dispose();

        using var engine2 = ObjectEngine.Open(_path);
        Assert.Equal(folderId, engine2.ResolvePath("/persist-folder"));
        Assert.Equal(fileId, engine2.ResolvePath("/persist-folder/persist-file"));

        byte[] buf = new byte[10];
        engine2.ReadAt(fileId, 0, buf);
        Assert.Equal("persistent"u8.ToArray(), buf);
    }
}
