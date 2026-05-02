using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// On-disk record for a node/object stored as a B-tree leaf value.
/// </summary>
public sealed class NodeRecord
{
    public ulong Id { get; set; }
    public ulong ParentId { get; set; }
    public ulong NameHash { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte NodeTypeFlags { get; set; }
    public long Size { get; set; }
    public uint ChildCount { get; set; }
    public long Created { get; set; } // Unix ms
    public long Modified { get; set; } // Unix ms
    public long ExtentListAddress { get; set; }
    public long MetadataBlockAddress { get; set; }
    public byte CompressionCodec { get; set; } // 0=None, 1=LZ4, 2=Zstd

    // Flag bits
    public const byte FlagHasData = 0x01;
    public const byte FlagHasChildren = 0x02;
    public const byte FlagIsDeleted = 0x04;

    public bool HasData => (NodeTypeFlags & FlagHasData) != 0;
    public bool HasChildren => (NodeTypeFlags & FlagHasChildren) != 0;
    public bool IsDeleted => (NodeTypeFlags & FlagIsDeleted) != 0;

    public byte[] Serialize()
    {
        int nameByteCount = System.Text.Encoding.UTF8.GetByteCount(Name);
        int size = 8 + 8 + 8 + 2 + nameByteCount + 1 + 8 + 4 + 8 + 8 + 8 + 8 + 1 + 3;
        byte[] data = new byte[size];
        var span = data.AsSpan();
        int offset = 0;

        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], Id); offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], ParentId); offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], NameHash); offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)nameByteCount); offset += 2;
        System.Text.Encoding.UTF8.GetBytes(Name, span.Slice(offset, nameByteCount)); offset += nameByteCount;
        span[offset++] = NodeTypeFlags;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], Size); offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], ChildCount); offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], Created); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], Modified); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], ExtentListAddress); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], MetadataBlockAddress); offset += 8;
        span[offset++] = CompressionCodec;
        // 3 bytes reserved (already zero)

        return data;
    }

    public static NodeRecord Deserialize(ReadOnlySpan<byte> data)
    {
        var rec = new NodeRecord();
        int offset = 0;

        rec.Id = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8;
        rec.ParentId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8;
        rec.NameHash = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8;
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]); offset += 2;
        rec.Name = System.Text.Encoding.UTF8.GetString(data.Slice(offset, nameLen)); offset += nameLen;
        rec.NodeTypeFlags = data[offset++];
        rec.Size = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        rec.ChildCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
        rec.Created = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        rec.Modified = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        rec.ExtentListAddress = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        rec.MetadataBlockAddress = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8;
        rec.CompressionCodec = data[offset++];

        return rec;
    }
}
