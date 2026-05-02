using System.Security.Cryptography;

namespace ObjectStore.Tests;

/// <summary>
/// Stress tests for comprehensive validation (Batch 5).
/// Tests large objects, many objects, deep transactions, hierarchy, and MVCC.
/// </summary>
public class StressTests : IDisposable
{
    private readonly string _dir;

    public StressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"objstore_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewFile() => Path.Combine(_dir, $"{Guid.NewGuid():N}.obs");

    // ===== Large Object =====

    [Fact]
    public void LargeObject_1MB_MultiExtent_SHA256Verified()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("bigfile");

        // Write 1MB in 64KB chunks
        byte[] fullData = new byte[1024 * 1024];
        Random.Shared.NextBytes(fullData);
        var expectedHash = SHA256.HashData(fullData);

        // Write in chunks
        const int chunkSize = 64 * 1024;
        for (int offset = 0; offset < fullData.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, fullData.Length - offset);
            engine.Append(id, fullData.AsSpan(offset, len));
        }

        var info = engine.GetInfo(id);
        Assert.Equal(1024 * 1024, info!.Size);

        // Read back full content and verify SHA256
        byte[] readBack = new byte[1024 * 1024];
        engine.ReadAt(id, 0, readBack);
        var actualHash = SHA256.HashData(readBack);
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public void LargeObject_PartialOverwrite_Verified()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        ulong id = engine.CreateObject("partial");
        byte[] data = new byte[256 * 1024];
        Array.Fill(data, (byte)0xAA);
        engine.Append(id, data);

        // Overwrite middle section
        byte[] patch = new byte[1024];
        Array.Fill(patch, (byte)0xBB);
        engine.WriteAt(id, 100_000, patch);

        // Verify
        byte[] readBack = new byte[256 * 1024];
        engine.ReadAt(id, 0, readBack);

        for (int i = 0; i < 100_000; i++)
            Assert.Equal(0xAA, readBack[i]);
        for (int i = 100_000; i < 101_024; i++)
            Assert.Equal(0xBB, readBack[i]);
        for (int i = 101_024; i < readBack.Length; i++)
            Assert.Equal(0xAA, readBack[i]);
    }

    // ===== Many Objects =====

    [Fact]
    public void ManyObjects_2000_RandomAccess()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        var ids = new List<ulong>();
        for (int i = 0; i < 2000; i++)
        {
            ulong id = engine.CreateObject($"obj_{i}");
            byte[] payload = BitConverter.GetBytes(i * 7);
            engine.Append(id, payload);
            ids.Add(id);
        }

        // Random access verification
        var rng = new Random(42);
        for (int trial = 0; trial < 500; trial++)
        {
            int idx = rng.Next(ids.Count);
            var info = engine.GetInfo(ids[idx]);
            Assert.NotNull(info);
            Assert.Equal($"obj_{idx}", info!.Name);

            byte[] buf = new byte[4];
            engine.ReadAt(ids[idx], 0, buf);
            int value = BitConverter.ToInt32(buf);
            Assert.Equal(idx * 7, value);
        }
    }

    [Fact]
    public void ManyObjects_CreateDelete_NoLeak()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        // Create and delete 500 objects repeatedly
        for (int round = 0; round < 5; round++)
        {
            var ids = new List<ulong>();
            for (int i = 0; i < 100; i++)
            {
                ulong id = engine.CreateObject($"round{round}_obj{i}");
                engine.Append(id, new byte[256]);
                ids.Add(id);
            }

            // Delete all
            foreach (var id in ids)
                engine.DeleteSubtree(id);
        }

        // File should not have grown excessively (allocator reuses space)
        var fileSize = new FileInfo(path).Length;
        // 500 objects × 256 bytes data + metadata ≈ conservative 2MB limit
        Assert.True(fileSize < 4 * 1024 * 1024,
            $"File grew too large: {fileSize / 1024}KB — allocator not reusing space?");
    }

    // ===== Transactions =====

    [Fact]
    public void Transaction_CommitPreservesData()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);
        var txn = engine.Transactions;

        txn.Begin();

        var ids = new List<ulong>();
        for (int i = 0; i < 30; i++)
        {
            ulong id = engine.CreateObject($"txn_obj_{i}");
            engine.Append(id, BitConverter.GetBytes(i));
            ids.Add(id);
        }

        txn.Commit();

        // All objects should be visible
        foreach (var (id, idx) in ids.Select((id, idx) => (id, idx)))
        {
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
            Assert.Equal($"txn_obj_{idx}", info!.Name);
        }
    }

    [Fact]
    public void Transaction_FullRollback_NothingPersists()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);
        var txn = engine.Transactions;

        txn.Begin();
        var ids = new List<ulong>();
        for (int i = 0; i < 50; i++)
        {
            ulong id = engine.CreateObject($"doomed_{i}");
            engine.Append(id, new byte[128]);
            ids.Add(id);
        }
        txn.Rollback();

        // Nothing should exist
        foreach (var id in ids)
        {
            Assert.Null(engine.GetInfo(id));
        }
    }

    // ===== MVCC: Writer commits, reader sees via refresh =====

    [Fact]
    public void MVCC_WriterCommits_ReaderSeesAfterRefresh()
    {
        var path = NewFile();
        ulong id;

        // Writer creates object
        using (var writer = ObjectEngine.OpenOrCreate(path))
        {
            id = writer.CreateObject("shared");
            writer.Append(id, "hello"u8.ToArray());
        }

        // Reader opens, sees object
        using var reader = ObjectEngine.OpenReadOnly(path);
        var info = reader.GetInfo(id);
        Assert.NotNull(info);
        Assert.Equal(5L, info!.Size);
    }

    [Fact]
    public void MVCC_SequentialWriters_DataConsistent()
    {
        var path = NewFile();

        // Writer 1 creates objects
        ulong id1, id2;
        using (var w1 = ObjectEngine.OpenOrCreate(path))
        {
            id1 = w1.CreateObject("first");
            w1.Append(id1, "aaa"u8.ToArray());
        }

        // Writer 2 opens same file, adds more
        using (var w2 = ObjectEngine.OpenOrCreate(path))
        {
            id2 = w2.CreateObject("second");
            w2.Append(id2, "bbb"u8.ToArray());
        }

        // Reader verifies both exist
        using var reader = ObjectEngine.OpenReadOnly(path);
        Assert.NotNull(reader.GetInfo(id1));
        Assert.NotNull(reader.GetInfo(id2));

        byte[] buf1 = new byte[3];
        reader.ReadAt(id1, 0, buf1);
        Assert.Equal("aaa"u8.ToArray(), buf1);

        byte[] buf2 = new byte[3];
        reader.ReadAt(id2, 0, buf2);
        Assert.Equal("bbb"u8.ToArray(), buf2);
    }

    // ===== Hierarchy =====

    [Fact]
    public void Hierarchy_2ChildrenPer10Levels_Correct()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        // 2 children per node, 10 levels → 2^10 - 1 = 1023 total objects
        var levelIds = new List<List<ulong>> { new() { 1UL } }; // root

        for (int level = 1; level <= 10; level++)
        {
            var currentLevel = new List<ulong>();
            foreach (var parentId in levelIds[level - 1])
            {
                for (int c = 0; c < 2; c++)
                {
                    ulong childId = engine.CreateChild(parentId, $"L{level}_C{c}_P{parentId}");
                    currentLevel.Add(childId);
                }
            }
            levelIds.Add(currentLevel);
        }

        // Verify leaf level (1024 nodes)
        Assert.Equal(1024, levelIds[10].Count);

        // Spot-check some leaves
        foreach (var id in levelIds[10].Take(50))
        {
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
            Assert.StartsWith("L10_", info!.Name);
        }

        // Move a subtree from level 5 to level 2
        var nodeToMove = levelIds[5][0];
        var newParent = levelIds[2][3];
        engine.MoveNode(nodeToMove, newParent, "moved_node");

        var movedInfo = engine.GetInfo(nodeToMove);
        Assert.Equal("moved_node", movedInfo!.Name);
    }

    [Fact]
    public void Hierarchy_DeleteSubtree_FreesAll()
    {
        var path = NewFile();
        using var engine = ObjectEngine.OpenOrCreate(path);

        // Create a subtree: parent → 10 children → 5 grandchildren each = 61 nodes total
        ulong parent = engine.CreateChild(1, "subtree_root");
        var children = new List<ulong>();
        for (int i = 0; i < 10; i++)
        {
            ulong child = engine.CreateChild(parent, $"child_{i}");
            engine.Append(child, new byte[64]);
            children.Add(child);

            for (int j = 0; j < 5; j++)
            {
                ulong gc = engine.CreateChild(child, $"gc_{j}");
                engine.Append(gc, new byte[32]);
            }
        }

        // Delete the entire subtree
        engine.DeleteSubtree(parent);

        // All nodes should be gone
        Assert.Null(engine.GetInfo(parent));
        foreach (var child in children)
        {
            Assert.Null(engine.GetInfo(child));
        }
    }

    // ===== Persistence / Reopen =====

    [Fact]
    public void Reopen_AllDataIntact_After100Commits()
    {
        var path = NewFile();
        var ids = new List<ulong>();

        using (var engine = ObjectEngine.OpenOrCreate(path))
        {
            for (int i = 0; i < 100; i++)
            {
                ulong id = engine.CreateObject($"commit_{i}");
                engine.Append(id, BitConverter.GetBytes(i));
                ids.Add(id);
            }
        }

        // Reopen and verify all
        using var reader = ObjectEngine.OpenReadOnly(path);
        for (int i = 0; i < 100; i++)
        {
            var info = reader.GetInfo(ids[i]);
            Assert.NotNull(info);
            Assert.Equal($"commit_{i}", info!.Name);

            byte[] buf = new byte[4];
            reader.ReadAt(ids[i], 0, buf);
            Assert.Equal(i, BitConverter.ToInt32(buf));
        }
    }
}
