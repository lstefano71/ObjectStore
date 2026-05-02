namespace ObjectStore;

/// <summary>
/// FNV-1a hash utility for computing name hashes used in B-tree keys.
/// Operates on raw UTF-8 bytes for case-sensitive comparison.
/// </summary>
public static class FnvHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Computes FNV-1a 64-bit hash over the given bytes.</summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        ulong hash = OffsetBasis;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= Prime;
        }
        return hash;
    }

    /// <summary>Computes FNV-1a hash of a string's UTF-8 representation.</summary>
    public static ulong ComputeString(string s)
    {
        Span<byte> buffer = stackalloc byte[256];
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(s);

        if (byteCount <= 256)
        {
            System.Text.Encoding.UTF8.GetBytes(s, buffer);
            return Compute(buffer[..byteCount]);
        }

        // Heap allocate for long strings
        byte[] heapBuffer = System.Text.Encoding.UTF8.GetBytes(s);
        return Compute(heapBuffer);
    }
}
