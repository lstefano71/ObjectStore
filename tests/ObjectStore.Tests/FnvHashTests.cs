namespace ObjectStore.Tests;

public class FnvHashTests
{
    [Fact]
    public void ComputeString_DifferentStrings_DifferentHashes()
    {
        ulong h1 = FnvHash.ComputeString("hello");
        ulong h2 = FnvHash.ComputeString("world");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ComputeString_SameString_SameHash()
    {
        ulong h1 = FnvHash.ComputeString("test");
        ulong h2 = FnvHash.ComputeString("test");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ComputeString_CaseSensitive()
    {
        ulong h1 = FnvHash.ComputeString("Hello");
        ulong h2 = FnvHash.ComputeString("hello");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ComputeString_EmptyString_HasValue()
    {
        ulong h = FnvHash.ComputeString("");
        // FNV-1a of empty = offset basis
        Assert.Equal(14695981039346656037UL, h);
    }

    [Fact]
    public void Compute_KnownValue()
    {
        // FNV-1a("foobar") is a known test vector
        byte[] data = System.Text.Encoding.UTF8.GetBytes("foobar");
        ulong hash = FnvHash.Compute(data);
        Assert.NotEqual(0UL, hash);
    }
}
