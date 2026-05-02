namespace ObjectStore.Tests;

public class SuperblockTests
{
    [Fact]
    public void Superblock_RoundTrip_Succeeds()
    {
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
            Generation = 42,
            BTreeRootAddress = 1024,
            BuddyRootAddress = 2048,
            ContainerSize = 1024 * 1024,
            Flags = Superblock.FlagEncryptionEnabled,
            NextNodeId = 100,
            RootNodeId = 1,
        };

        Span<byte> buffer = stackalloc byte[FormatConstants.SuperblockSize];
        sb.WriteTo(buffer);

        Assert.True(Superblock.TryReadFrom(buffer, out var loaded));
        Assert.Equal(sb.VersionMajor, loaded.VersionMajor);
        Assert.Equal(sb.VersionMinor, loaded.VersionMinor);
        Assert.Equal(sb.Generation, loaded.Generation);
        Assert.Equal(sb.BTreeRootAddress, loaded.BTreeRootAddress);
        Assert.Equal(sb.BuddyRootAddress, loaded.BuddyRootAddress);
        Assert.Equal(sb.ContainerSize, loaded.ContainerSize);
        Assert.Equal(sb.Flags, loaded.Flags);
        Assert.Equal(sb.NextNodeId, loaded.NextNodeId);
        Assert.Equal(sb.RootNodeId, loaded.RootNodeId);
    }

    [Fact]
    public void Superblock_InvalidMagic_FailsRead()
    {
        Span<byte> buffer = stackalloc byte[FormatConstants.SuperblockSize];
        buffer.Clear();
        "BADMAGIC\0"u8.CopyTo(buffer);

        Assert.False(Superblock.TryReadFrom(buffer, out _));
    }

    [Fact]
    public void Superblock_CorruptedChecksum_FailsRead()
    {
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = FormatConstants.VersionMinor,
            Generation = 1,
        };

        byte[] buffer = new byte[FormatConstants.SuperblockSize];
        sb.WriteTo(buffer);

        // Corrupt a byte in the payload area
        buffer[20] ^= 0xFF;

        Assert.False(Superblock.TryReadFrom(buffer, out _));
    }

    [Fact]
    public void Superblock_IsVersionCompatible_MatchesMajor()
    {
        var sb = new Superblock
        {
            VersionMajor = FormatConstants.VersionMajor,
            VersionMinor = 99, // minor doesn't matter
        };
        Assert.True(sb.IsVersionCompatible);

        sb.VersionMajor = 99;
        Assert.False(sb.IsVersionCompatible);
    }

    [Fact]
    public void Superblock_ZeroBuffer_FailsRead()
    {
        Span<byte> buffer = stackalloc byte[FormatConstants.SuperblockSize];
        buffer.Clear();
        Assert.False(Superblock.TryReadFrom(buffer, out _));
    }
}
