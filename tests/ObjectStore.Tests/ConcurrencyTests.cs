using System.Text;

namespace ObjectStore.Tests;

/// <summary>
/// Tests verifying multi-handle concurrent access to the same file within a single process.
/// Two ObjectEngine instances open the same file simultaneously, acting as independent
/// hybrid reader+writer clients (simulating multi-process behavior in-process).
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly string _path;

    public ConcurrencyTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_conc_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void TwoHandles_WriterAndReader_RefreshSeesCommittedData()
    {
        // Writer creates an object and writes data
        using var writer = ObjectEngine.Create(_path);
        using var reader = ObjectEngine.Open(_path);

        var id = writer.CreateObject("hello");
        writer.Append(id, Encoding.UTF8.GetBytes("world"));

        // Reader doesn't see it yet (stale state)
        Assert.False(reader.Exists(id));

        // After refresh, reader sees the committed object
        reader.Refresh();
        Assert.True(reader.Exists(id));
        var buf = new byte[5];
        int read = reader.ReadAt(id, 0, buf);
        Assert.Equal(5, read);
        Assert.Equal("world", Encoding.UTF8.GetString(buf));
    }

    [Fact]
    public void TwoHandles_BothWriters_SerializedCommits()
    {
        using var engine1 = ObjectEngine.Create(_path);
        using var engine2 = ObjectEngine.Open(_path);

        // Engine1 creates an object
        var id1 = engine1.CreateObject("obj1");
        engine1.Append(id1, new byte[] { 1, 2, 3 });

        // Engine2 refreshes and creates its own object
        engine2.Refresh();
        var id2 = engine2.CreateObject("obj2");
        engine2.Append(id2, new byte[] { 4, 5, 6 });

        // Engine1 refreshes — should see both objects
        engine1.Refresh();
        Assert.True(engine1.Exists(id1));
        Assert.True(engine1.Exists(id2));

        var buf = new byte[3];
        engine1.ReadAt(id2, 0, buf);
        Assert.Equal(new byte[] { 4, 5, 6 }, buf);
    }

    [Fact]
    public void TwoHandles_ConcurrentTransactions_Serialized()
    {
        using var engine1 = ObjectEngine.Create(_path);
        using var engine2 = ObjectEngine.Open(_path);

        // Engine1 begins a transaction and creates objects
        engine1.BeginTransaction();
        var id1 = engine1.CreateObject("txn1_obj1");
        var id2 = engine1.CreateObject("txn1_obj2");

        // Engine2 cannot see uncommitted objects even after refresh
        engine2.Refresh();
        Assert.False(engine2.Exists(id1));

        // Engine1 commits
        engine1.CommitTransaction();

        // Now engine2 can see them after refresh
        engine2.Refresh();
        Assert.True(engine2.Exists(id1));
        Assert.True(engine2.Exists(id2));
    }

    [Fact]
    public void TwoHandles_RollbackNotVisibleToOther()
    {
        using var engine1 = ObjectEngine.Create(_path);

        // Create a baseline object so the file is valid
        var baseId = engine1.CreateObject("base");

        using var engine2 = ObjectEngine.Open(_path);

        // Engine1 starts a transaction, creates object, then rolls back
        engine1.BeginTransaction();
        var id = engine1.CreateObject("will_rollback");
        engine1.RollbackTransaction();

        // Engine2 refreshes — should NOT see the rolled-back object
        engine2.Refresh();
        Assert.False(engine2.Exists(id));
        Assert.True(engine2.Exists(baseId));
    }

    [Fact]
    public void TwoHandles_LargeDataIntegrity()
    {
        using var writer = ObjectEngine.Create(_path);
        using var reader = ObjectEngine.Open(_path);

        // Writer creates object with 64KB of patterned data
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251); // prime to avoid alignment tricks

        var id = writer.CreateObject("big");
        writer.Append(id, data);

        // Reader refreshes and reads back
        reader.Refresh();
        var readBack = new byte[65536];
        int bytesRead = reader.ReadAt(id, 0, readBack);
        Assert.Equal(65536, bytesRead);
        Assert.Equal(data, readBack);
    }

    [Fact]
    public void TwoHandles_AlternatingWritesPreserveAllData()
    {
        using var engine1 = ObjectEngine.Create(_path);
        using var engine2 = ObjectEngine.Open(_path);

        var ids = new List<ulong>();

        // Alternate: engine1 writes, engine2 writes, repeat
        for (int i = 0; i < 20; i++)
        {
            var eng = (i % 2 == 0) ? engine1 : engine2;
            // Refresh before writing to pick up the other's changes
            eng.Refresh();
            var id = eng.CreateObject($"obj_{i}");
            eng.Append(id, BitConverter.GetBytes(i));
            ids.Add(id);
        }

        // Both engines refresh and verify all objects
        engine1.Refresh();
        engine2.Refresh();

        for (int i = 0; i < 20; i++)
        {
            Assert.True(engine1.Exists(ids[i]), $"engine1 missing obj_{i}");
            Assert.True(engine2.Exists(ids[i]), $"engine2 missing obj_{i}");

            var buf = new byte[4];
            engine1.ReadAt(ids[i], 0, buf);
            Assert.Equal(i, BitConverter.ToInt32(buf));
        }
    }

    [Fact]
    public void TwoHandles_DeleteVisibleAfterRefresh()
    {
        using var engine1 = ObjectEngine.Create(_path);
        var id = engine1.CreateObject("to_delete");
        engine1.Append(id, new byte[] { 99 });

        using var engine2 = ObjectEngine.Open(_path);
        Assert.True(engine2.Exists(id));

        // Engine1 deletes the object
        engine1.DeleteObject(id);

        // Engine2 still sees it (stale)
        Assert.True(engine2.Exists(id));

        // After refresh, it's gone
        engine2.Refresh();
        Assert.False(engine2.Exists(id));
    }

    [Fact]
    public void ThreeHandles_MultipleWritersAndReader()
    {
        using var writer1 = ObjectEngine.Create(_path);
        using var writer2 = ObjectEngine.Open(_path);
        using var reader = ObjectEngine.Open(_path);

        // Writer1 creates objects
        var id1 = writer1.CreateObject("w1");
        writer1.Append(id1, Encoding.UTF8.GetBytes("from_writer1"));

        // Writer2 refreshes and creates objects
        writer2.Refresh();
        var id2 = writer2.CreateObject("w2");
        writer2.Append(id2, Encoding.UTF8.GetBytes("from_writer2"));

        // Reader refreshes — should see both
        reader.Refresh();
        Assert.True(reader.Exists(id1));
        Assert.True(reader.Exists(id2));

        var buf1 = new byte[12];
        reader.ReadAt(id1, 0, buf1);
        Assert.Equal("from_writer1", Encoding.UTF8.GetString(buf1));

        var buf2 = new byte[12];
        reader.ReadAt(id2, 0, buf2);
        Assert.Equal("from_writer2", Encoding.UTF8.GetString(buf2));
    }

    [Fact]
    public void ManyAlternatingCommits_SpaceDoesNotLeakBetweenHandles()
    {
        using var engine1 = ObjectEngine.Create(_path);
        using var engine2 = ObjectEngine.Open(_path);

        // Create and delete many objects alternating between handles
        for (int i = 0; i < 100; i++)
        {
            var eng = (i % 2 == 0) ? engine1 : engine2;
            eng.Refresh();
            var id = eng.CreateObject($"temp_{i}");
            eng.Append(id, new byte[1024]);
            eng.Refresh(); // ensure latest state before delete
            eng.DeleteObject(id);
        }

        var fileSize = new FileInfo(_path).Length;
        // After 100 create/delete cycles across two handles, file should not grow excessively
        // Baseline for an empty store is ~1MB, allow up to 2MB
        Assert.True(fileSize < 2 * 1024 * 1024,
            $"File grew to {fileSize / 1024}KB after 100 create/delete cycles across two handles");
    }

    [Fact]
    public void ConcurrentWriters_LockSerializesAccess()
    {
        using var engine1 = ObjectEngine.Create(_path);
        using var engine2 = ObjectEngine.Open(_path);

        // Start a transaction on engine1 (holds the lock)
        engine1.BeginTransaction();
        engine1.CreateObject("holding_lock");

        // Engine2 trying to write should eventually succeed after engine1 commits
        // (but with a very short timeout, it would fail)
        // We test the happy path: commit engine1, then engine2 can write
        engine1.CommitTransaction();

        engine2.Refresh();
        var id = engine2.CreateObject("after_lock_released");
        Assert.True(engine2.Exists(id));

        engine1.Refresh();
        Assert.True(engine1.Exists(id));
    }
}
