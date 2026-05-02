namespace ObjectStore.Tests;

public class ContainerFileTests : IDisposable
{
    private readonly string _tempPath;

    public ContainerFileTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"objstore_test_{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void Create_NewFile_Succeeds()
    {
        using var file = ContainerFile.Create(_tempPath);
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Create_ExistingFile_Throws()
    {
        File.WriteAllText(_tempPath, "exists");
        Assert.Throws<IOException>(() => ContainerFile.Create(_tempPath));
    }

    [Fact]
    public void WriteRaw_ReadRaw_RoundTrip()
    {
        using var file = ContainerFile.Create(_tempPath);
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        file.WriteRaw(0, data);

        byte[] read = new byte[8];
        file.ReadRaw(0, read);
        Assert.Equal(data, read);
    }

    [Fact]
    public void EnsureSize_GrowsFile()
    {
        using var file = ContainerFile.Create(_tempPath);
        file.EnsureSize(4096);
        Assert.True(file.FileSize >= 4096);
    }

    [Fact]
    public void WriteBlock_ReadBlock_RoundTrip()
    {
        using var file = ContainerFile.Create(_tempPath);
        file.EnsureSize(FormatConstants.DataRegionOffset + 256);

        byte[] payload = [10, 20, 30, 40, 50];
        long address = FormatConstants.DataRegionOffset;
        int order = 0; // 64-byte block

        file.WriteBlock(address, order, payload);
        byte[] result = file.ReadBlock(address, order);

        Assert.Equal(payload, result[..5]);
    }

    [Fact]
    public void WriteBlock_CorruptedBlock_ThrowsOnRead()
    {
        using var file = ContainerFile.Create(_tempPath);
        file.EnsureSize(FormatConstants.DataRegionOffset + 256);

        byte[] payload = [1, 2, 3];
        long address = FormatConstants.DataRegionOffset;
        file.WriteBlock(address, 0, payload);

        // Corrupt a byte in the payload area
        byte[] corrupt = [0xFF];
        file.WriteRaw(address + FormatConstants.BlockHeaderSize + 1, corrupt);

        Assert.Throws<BlockCorruptedException>(() => file.ReadBlock(address, 0));
    }

    [Fact]
    public void FileGrowth_ExponentialThenLinear()
    {
        using var file = ContainerFile.Create(_tempPath);
        file.EnsureSize(100);
        long firstSize = file.FileSize;
        Assert.True(firstSize >= 100);

        // Should have grown to at least DataRegionOffset
        Assert.True(firstSize >= FormatConstants.DataRegionOffset);
    }
}
