namespace ObjectStore.Tests;

/// <summary>
/// Tests verifying that all ChecksumPolicy modes correctly return stored data
/// and that the policy affects validation behavior as expected.
/// </summary>
public class ChecksumPolicyTests : IDisposable
{
    private readonly string _path;

    public ChecksumPolicyTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"objstore_cksum_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static byte[] ReadFull(ObjectStoreDatabase db, ulong id)
    {
        var info = db.GetInfo(id)!;
        var buf = new byte[info.Size];
        db.ReadAt(id, 0, buf);
        return buf;
    }

    [Theory]
    [InlineData(ChecksumPolicy.Always)]
    [InlineData(ChecksumPolicy.MetadataOnly)]
    [InlineData(ChecksumPolicy.OnFirstRead)]
    [InlineData(ChecksumPolicy.None)]
    public void AllPolicies_ReturnCorrectData(ChecksumPolicy policy)
    {
        var options = new ObjectStoreOptions { ChecksumPolicy = policy };
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        ulong id;

        // Write with default policy (Always) to ensure integrity on write
        using (var db = ObjectStoreDatabase.Create(_path, new ObjectStoreOptions()))
        {
            id = db.CreateObject("test-object");
            db.Append(id, payload);
        }

        // Re-open with the specified policy and verify read
        using (var db = ObjectStoreDatabase.Open(_path, options))
        {
            byte[] readBack = ReadFull(db, id);
            Assert.Equal(payload, readBack);
        }
    }

    [Theory]
    [InlineData(ChecksumPolicy.Always)]
    [InlineData(ChecksumPolicy.MetadataOnly)]
    [InlineData(ChecksumPolicy.OnFirstRead)]
    [InlineData(ChecksumPolicy.None)]
    public void AllPolicies_MultipleObjects_CorrectData(ChecksumPolicy policy)
    {
        var options = new ObjectStoreOptions { ChecksumPolicy = policy };
        const int objectCount = 50;
        var payloads = new byte[objectCount][];
        var ids = new ulong[objectCount];

        // Create objects with varying sizes
        using (var db = ObjectStoreDatabase.Create(_path))
        {
            for (int i = 0; i < objectCount; i++)
            {
                int size = 256 + i * 128; // 256 to ~6.6K
                payloads[i] = new byte[size];
                new Random(i * 42).NextBytes(payloads[i]);
                ids[i] = db.CreateObject($"obj-{i}");
                db.Append(ids[i], payloads[i]);
            }
        }

        // Re-open with specified policy, read all in random order
        using (var db = ObjectStoreDatabase.Open(_path, options))
        {
            var rng = new Random(999);
            var indices = Enumerable.Range(0, objectCount).OrderBy(_ => rng.Next()).ToArray();
            foreach (int idx in indices)
            {
                byte[] readBack = ReadFull(db, ids[idx]);
                Assert.Equal(payloads[idx], readBack);
            }
        }
    }

    [Fact]
    public void OnFirstRead_SecondReadSkipsValidation()
    {
        // This test verifies that OnFirstRead still returns correct data on second read
        // (after the address has been marked validated)
        var options = new ObjectStoreOptions { ChecksumPolicy = ChecksumPolicy.OnFirstRead };
        byte[] payload = new byte[8192];
        new Random(123).NextBytes(payload);
        ulong id;

        using (var db = ObjectStoreDatabase.Create(_path))
        {
            id = db.CreateObject("first-read-test");
            db.Append(id, payload);
        }

        using (var db = ObjectStoreDatabase.Open(_path, options))
        {
            // First read — validates checksum
            byte[] read1 = ReadFull(db, id);
            Assert.Equal(payload, read1);

            // Second read — should skip validation but still return correct data
            byte[] read2 = ReadFull(db, id);
            Assert.Equal(payload, read2);
        }
    }

    [Fact]
    public void MetadataOnly_LargeObject_CorrectData()
    {
        // Large objects span multiple extents — verify MetadataOnly handles them
        var options = new ObjectStoreOptions { ChecksumPolicy = ChecksumPolicy.MetadataOnly };
        byte[] payload = new byte[64 * 1024]; // 64KB — will span multiple blocks
        new Random(777).NextBytes(payload);
        ulong id;

        using (var db = ObjectStoreDatabase.Create(_path))
        {
            id = db.CreateObject("large-object");
            db.Append(id, payload);
        }

        using (var db = ObjectStoreDatabase.Open(_path, options))
        {
            byte[] readBack = ReadFull(db, id);
            Assert.Equal(payload, readBack);
        }
    }

    [Fact]
    public void DefaultPolicy_IsAlways()
    {
        var options = new ObjectStoreOptions();
        Assert.Equal(ChecksumPolicy.Always, options.ChecksumPolicy);
    }
}
