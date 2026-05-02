namespace ObjectStore.Tests;

/// <summary>
/// Regression tests for code review findings — Batch 1 (critical data integrity).
/// Issue 1: WriteAt multi-extent data loss
/// Issue 2: B-tree node block overflow at ~40 objects
/// </summary>
public class RegressionBatch1Tests : IDisposable
{
    private readonly string _path;
    private ObjectEngine? _engine;

    public RegressionBatch1Tests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_regr1_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        _engine?.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private ObjectEngine CreateEngine()
    {
        _engine = ObjectEngine.Create(_path);
        return _engine;
    }

    // --- Issue 1: WriteAt multi-extent ---

    [Fact]
    public void WriteAt_SpanningTwoExtents_AllBytesWritten()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("multi-ext");

        // Order 0 payload = 48 bytes. Write 100 bytes → 3 extents (48+48+4).
        byte[] initial = new byte[100];
        engine.Append(id, initial);

        // Now overwrite bytes 40..89 (spanning from extent 0 into extent 1)
        byte[] writeData = new byte[50];
        for (int i = 0; i < 50; i++) writeData[i] = (byte)(i + 1);

        engine.WriteAt(id, 40, writeData);

        // Read back entire object and verify
        byte[] readBuf = new byte[100];
        int read = engine.ReadAt(id, 0, readBuf);
        Assert.Equal(100, read);

        // Bytes 0..39 should be zero (original)
        for (int i = 0; i < 40; i++)
            Assert.Equal(0, readBuf[i]);

        // Bytes 40..89 should be our written data
        for (int i = 0; i < 50; i++)
            Assert.Equal((byte)(i + 1), readBuf[40 + i]);

        // Bytes 90..99 should be zero (original)
        for (int i = 90; i < 100; i++)
            Assert.Equal(0, readBuf[i]);
    }

    [Fact]
    public void WriteAt_SpanningThreeExtents_AllBytesWritten()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("big-write");

        // Create object with 200 bytes (5 extents of 48 bytes each, last partial)
        byte[] initial = new byte[200];
        engine.Append(id, initial);

        // Overwrite bytes 20..179 (spanning extents 0,1,2,3 — 160 bytes)
        byte[] writeData = new byte[160];
        for (int i = 0; i < 160; i++) writeData[i] = (byte)((i * 7 + 3) & 0xFF);

        engine.WriteAt(id, 20, writeData);

        // Read back and verify
        byte[] readBuf = new byte[200];
        int read = engine.ReadAt(id, 0, readBuf);
        Assert.Equal(200, read);

        // Check prefix zeros
        for (int i = 0; i < 20; i++)
            Assert.Equal(0, readBuf[i]);

        // Check written region
        for (int i = 0; i < 160; i++)
            Assert.Equal((byte)((i * 7 + 3) & 0xFF), readBuf[20 + i]);

        // Check suffix zeros
        for (int i = 180; i < 200; i++)
            Assert.Equal(0, readBuf[i]);
    }

    [Fact]
    public void WriteAt_StartInMiddleOfSecondExtent_CorrectData()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("mid-ext");

        // 150 bytes → 4 extents (48+48+48+6)
        byte[] initial = new byte[150];
        engine.Append(id, initial);

        // Write 30 bytes starting at offset 60 (which is 12 bytes into extent 1)
        byte[] writeData = new byte[30];
        for (int i = 0; i < 30; i++) writeData[i] = 0xAB;

        engine.WriteAt(id, 60, writeData);

        byte[] readBuf = new byte[150];
        engine.ReadAt(id, 0, readBuf);

        // Bytes 60..89 should be 0xAB
        for (int i = 60; i < 90; i++)
            Assert.Equal(0xAB, readBuf[i]);

        // Bytes before and after should be zero
        for (int i = 0; i < 60; i++)
            Assert.Equal(0, readBuf[i]);
        for (int i = 90; i < 150; i++)
            Assert.Equal(0, readBuf[i]);
    }

    [Fact]
    public void WriteAt_EntireObjectOverwrite_AllBytesCorrect()
    {
        var engine = CreateEngine();
        ulong id = engine.CreateObject("full-overwrite");

        byte[] initial = new byte[200];
        engine.Append(id, initial);

        // Overwrite entire object
        byte[] writeData = new byte[200];
        Random.Shared.NextBytes(writeData);

        engine.WriteAt(id, 0, writeData);

        byte[] readBuf = new byte[200];
        engine.ReadAt(id, 0, readBuf);

        Assert.Equal(writeData, readBuf);
    }

    // --- Issue 2: B-tree capacity ---

    [Fact]
    public void BTree_Insert100ObjectsUnderSameParent_AllRetrievable()
    {
        var engine = CreateEngine();
        var ids = new List<ulong>();

        for (int i = 0; i < 100; i++)
        {
            ulong id = engine.CreateObject($"obj_{i:D4}");
            ids.Add(id);
        }

        // Verify all are retrievable
        foreach (var id in ids)
        {
            Assert.True(engine.Exists(id));
            var info = engine.GetInfo(id);
            Assert.NotNull(info);
        }
    }

    [Fact]
    public void BTree_Insert200Objects_ForcesMultipleSplits()
    {
        var engine = CreateEngine();
        var ids = new List<ulong>();

        for (int i = 0; i < 200; i++)
        {
            ulong id = engine.CreateObject($"node_{i:D4}");
            ids.Add(id);
        }

        // Verify all retrievable
        foreach (var id in ids)
            Assert.True(engine.Exists(id));

        // Delete half and verify remaining
        for (int i = 0; i < 100; i++)
            engine.DeleteObject(ids[i]);

        for (int i = 100; i < 200; i++)
            Assert.True(engine.Exists(ids[i]));

        for (int i = 0; i < 100; i++)
            Assert.False(engine.Exists(ids[i]));
    }

    [Fact]
    public void BTree_Insert500_Delete250_VerifyRemaining()
    {
        var engine = CreateEngine();
        var ids = new List<ulong>();

        for (int i = 0; i < 500; i++)
        {
            ulong id = engine.CreateObject($"stress_{i:D4}");
            ids.Add(id);
        }

        // Delete odd-indexed
        for (int i = 1; i < 500; i += 2)
            engine.DeleteObject(ids[i]);

        // Verify even-indexed still exist
        for (int i = 0; i < 500; i += 2)
        {
            Assert.True(engine.Exists(ids[i]));
        }

        // Verify odd-indexed are gone
        for (int i = 1; i < 500; i += 2)
        {
            Assert.False(engine.Exists(ids[i]));
        }
    }
}
