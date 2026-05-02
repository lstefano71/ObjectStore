using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// Composite B-tree key: (parent_id, name_hash).
/// Ordered first by parent_id, then by name_hash.
/// </summary>
public readonly record struct BTreeKey(ulong ParentId, ulong NameHash) : IComparable<BTreeKey>
{
    public const int Size = 16;

    public int CompareTo(BTreeKey other)
    {
        int cmp = ParentId.CompareTo(other.ParentId);
        return cmp != 0 ? cmp : NameHash.CompareTo(other.NameHash);
    }

    public void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, ParentId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], NameHash);
    }

    public byte[] Serialize()
    {
        var buf = new byte[Size];
        WriteTo(buf);
        return buf;
    }

    public static BTreeKey ReadFrom(ReadOnlySpan<byte> buffer)
    {
        ulong parentId = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        ulong nameHash = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
        return new BTreeKey(parentId, nameHash);
    }

    public static bool operator <(BTreeKey left, BTreeKey right) => left.CompareTo(right) < 0;
    public static bool operator >(BTreeKey left, BTreeKey right) => left.CompareTo(right) > 0;
    public static bool operator <=(BTreeKey left, BTreeKey right) => left.CompareTo(right) <= 0;
    public static bool operator >=(BTreeKey left, BTreeKey right) => left.CompareTo(right) >= 0;
}
