namespace ObjectStore.Tests;

public class SuperblockManagerTests : IDisposable
{
    private readonly string _tempPath;
    private readonly ContainerFile _file;

    public SuperblockManagerTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"objstore_test_{Guid.NewGuid():N}.bin");
        _file = ContainerFile.Create(_tempPath);
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(_tempPath);
    }

    [Fact]
    public void Initialize_ThenLoad_Succeeds()
    {
        var manager = new SuperblockManager(_file);
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
            BTreeRootAddress = 2048,
            ContainerSize = 65536,
            NextNodeId = 2,
            RootNodeId = 1,
        };

        manager.Initialize(sb);

        // Reload
        var manager2 = new SuperblockManager(_file);
        Assert.True(manager2.TryLoad());
        Assert.Equal((ulong)1, manager2.Active.Generation);
        Assert.Equal((ulong)2048, manager2.Active.BTreeRootAddress);
    }

    [Fact]
    public void Commit_IncrementsGeneration()
    {
        var manager = new SuperblockManager(_file);
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
        };

        manager.Initialize(sb);
        Assert.Equal((ulong)1, manager.Active.Generation);

        sb.BTreeRootAddress = 4096;
        manager.Commit(sb);
        Assert.Equal((ulong)2, manager.Active.Generation);
        Assert.Equal((ulong)4096, manager.Active.BTreeRootAddress);
    }

    [Fact]
    public void Commit_AlternatesSlots()
    {
        var manager = new SuperblockManager(_file);
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
        };

        manager.Initialize(sb);
        int slot1 = manager.ActiveSlot;

        manager.Commit(sb);
        int slot2 = manager.ActiveSlot;
        Assert.NotEqual(slot1, slot2);

        manager.Commit(sb);
        Assert.Equal(slot1, manager.ActiveSlot);
    }

    [Fact]
    public void TryLoad_EmptyFile_ReturnsFalse()
    {
        var manager = new SuperblockManager(_file);
        _file.EnsureSize(FormatConstants.DataRegionOffset);
        Assert.False(manager.TryLoad());
    }

    [Fact]
    public void TryLoad_SelectsHigherGeneration()
    {
        var manager = new SuperblockManager(_file);
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
            BTreeRootAddress = 1000,
        };

        manager.Initialize(sb);
        sb.BTreeRootAddress = 2000;
        manager.Commit(sb);
        sb.BTreeRootAddress = 3000;
        manager.Commit(sb);

        // Reload - should pick gen 3
        var manager2 = new SuperblockManager(_file);
        Assert.True(manager2.TryLoad());
        Assert.Equal((ulong)3, manager2.Active.Generation);
        Assert.Equal((ulong)3000, manager2.Active.BTreeRootAddress);
    }
}
