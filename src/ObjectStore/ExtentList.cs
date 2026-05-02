using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// Manages the extent list for an object — a list of (blockAddress, order) pairs
/// that describe where the object's data lives on disk.
/// </summary>
public sealed class ExtentList
{
    public List<(long Address, int Order)> Extents { get; } = new();

    /// <summary>Total data capacity in bytes across all extents.</summary>
    public long TotalCapacity => Extents.Sum(e => (long)FormatConstants.PayloadSizeForOrder(e.Order));

    public byte[] Serialize()
    {
        // Format: [count: u32][entries: count × (address: i64, order: u8)]
        int size = 4 + Extents.Count * 9;
        byte[] data = new byte[size];
        var span = data.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span, Extents.Count);
        int offset = 4;
        foreach (var (address, order) in Extents)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], address);
            offset += 8;
            span[offset++] = (byte)order;
        }
        return data;
    }

    public static ExtentList Deserialize(ReadOnlySpan<byte> data)
    {
        var list = new ExtentList();
        if (data.Length < 4) return list;

        int count = BinaryPrimitives.ReadInt32LittleEndian(data);
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            long address = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
            offset += 8;
            int order = data[offset++];
            list.Extents.Add((address, order));
        }
        return list;
    }
}
