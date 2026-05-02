namespace ObjectStore.Tests;

public class BuddyAllocatorTests : IDisposable
{
    private readonly string _tempPath;
    private readonly ContainerFile _file;
    private readonly BuddyAllocator _allocator;

    public BuddyAllocatorTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"objstore_test_{Guid.NewGuid():N}.bin");
        _file = ContainerFile.Create(_tempPath);
        _file.EnsureSize(FormatConstants.DataRegionOffset);
        _allocator = new BuddyAllocator(_file, FormatConstants.DataRegionOffset);
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(_tempPath);
    }

    [Fact]
    public void Allocate_ReturnsValidAddress()
    {
        long addr = _allocator.Allocate(0);
        Assert.True(addr >= FormatConstants.DataRegionOffset);
    }

    [Fact]
    public void Allocate_ConsecutiveAllocations_DontOverlap()
    {
        long addr1 = _allocator.Allocate(0); // 64 bytes
        long addr2 = _allocator.Allocate(0); // 64 bytes

        Assert.NotEqual(addr1, addr2);
        // They should not overlap
        Assert.True(Math.Abs(addr1 - addr2) >= FormatConstants.BlockSizeForOrder(0));
    }

    [Fact]
    public void Free_ThenAllocate_ReusesBlock()
    {
        long addr1 = _allocator.Allocate(0);
        _allocator.Free(addr1, 0);

        long addr2 = _allocator.Allocate(0);
        Assert.Equal(addr1, addr2);
    }

    [Fact]
    public void Allocate_LargerOrder_SplitsFromBiggerBlock()
    {
        // Allocate order 2 (256 bytes)
        long addr = _allocator.Allocate(2);
        Assert.True(addr >= FormatConstants.DataRegionOffset);

        // Allocating order 0 should use a split piece if available
        long small = _allocator.Allocate(0);
        Assert.True(small >= FormatConstants.DataRegionOffset);
    }

    [Fact]
    public void Free_Coalesces_WithBuddy()
    {
        // Allocate two order-0 blocks that are buddies
        long addr1 = _allocator.Allocate(1); // get a 128-byte aligned block
        _allocator.Free(addr1, 1);

        // Now allocate two order-0 blocks from it
        long a = _allocator.Allocate(0);
        long b = _allocator.Allocate(0);

        // Free both — should coalesce back to order 1
        _allocator.Free(a, 0);
        _allocator.Free(b, 0);

        // Allocating order 1 should succeed from the coalesced block
        long reused = _allocator.Allocate(1);
        Assert.Equal(Math.Min(a, b), reused);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        long addr1 = _allocator.Allocate(0);
        long addr2 = _allocator.Allocate(1);
        _allocator.Free(addr1, 0);

        byte[] state = _allocator.Serialize();

        // Create a new allocator and deserialize
        var allocator2 = new BuddyAllocator(_file, FormatConstants.DataRegionOffset);
        allocator2.Deserialize(state);

        Assert.Equal(_allocator.DataRegionEnd, allocator2.DataRegionEnd);
        Assert.Equal(_allocator.FreeBlockCount, allocator2.FreeBlockCount);

        // The free block should be reusable
        long reused = allocator2.Allocate(0);
        Assert.Equal(addr1, reused);
    }

    [Fact]
    public void GetBuddyAddress_IsSymmetric()
    {
        long addr = FormatConstants.DataRegionOffset;
        long buddy = BuddyAllocator.GetBuddyAddress(addr, 0);
        long buddyOfBuddy = BuddyAllocator.GetBuddyAddress(buddy, 0);
        Assert.Equal(addr, buddyOfBuddy);
    }

    [Fact]
    public void Allocate_InvalidOrder_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _allocator.Allocate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _allocator.Allocate(18));
    }

    [Fact]
    public void Free_InvalidOrder_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _allocator.Free(FormatConstants.DataRegionOffset, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _allocator.Free(FormatConstants.DataRegionOffset, 18));
    }
}
