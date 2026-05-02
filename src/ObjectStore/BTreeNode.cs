using System.Buffers.Binary;

namespace ObjectStore;

/// <summary>
/// Represents a B-tree node stored in a single buddy-allocated block.
/// 
/// On-disk layout:
///   [0]       u8   nodeType (0=leaf, 1=internal)
///   [1..2]    u16  keyCount
///   [3]       u8   reserved
///   [4..]     keys: keyCount × 16 bytes (BTreeKey)
///   After keys:
///     If leaf: values array (keyCount × valueSize bytes)
///     If internal: child pointers ((keyCount+1) × 8 bytes, u64 addresses)
/// </summary>
public sealed class BTreeNode
{
    public const byte TypeLeaf = 0;
    public const byte TypeInternal = 1;

    public byte NodeType { get; set; }
    public List<BTreeKey> Keys { get; } = new();

    // Leaf: values corresponding to each key
    public List<byte[]> Values { get; } = new();

    // Internal: child block addresses (Keys.Count + 1)
    public List<long> Children { get; } = new();

    /// <summary>The block address where this node is stored (0 if not yet persisted).</summary>
    public long Address { get; set; }

    public bool IsLeaf => NodeType == TypeLeaf;
    public int KeyCount => Keys.Count;

    /// <summary>
    /// Serializes this node to a byte array for storage in a buddy block.
    /// </summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(NodeType);
        writer.Write((ushort)Keys.Count);
        writer.Write((byte)0); // reserved

        // Keys
        Span<byte> keyBuf = stackalloc byte[BTreeKey.Size];
        foreach (var key in Keys)
        {
            key.WriteTo(keyBuf);
            writer.Write(keyBuf);
        }

        if (IsLeaf)
        {
            // Values: length-prefixed byte arrays
            foreach (var value in Values)
            {
                writer.Write((ushort)value.Length);
                writer.Write(value);
            }
        }
        else
        {
            // Children: (keyCount + 1) × u64
            foreach (var child in Children)
            {
                writer.Write(child);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a node from a byte array.
    /// </summary>
    public static BTreeNode Deserialize(ReadOnlySpan<byte> data)
    {
        var node = new BTreeNode();
        int offset = 0;

        node.NodeType = data[offset++];
        ushort keyCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        offset++; // reserved

        // Keys
        for (int i = 0; i < keyCount; i++)
        {
            node.Keys.Add(BTreeKey.ReadFrom(data[offset..]));
            offset += BTreeKey.Size;
        }

        if (node.IsLeaf)
        {
            // Values
            for (int i = 0; i < keyCount; i++)
            {
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                offset += 2;
                node.Values.Add(data.Slice(offset, len).ToArray());
                offset += len;
            }
        }
        else
        {
            // Children
            for (int i = 0; i <= keyCount; i++)
            {
                long addr = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
                offset += 8;
                node.Children.Add(addr);
            }
        }

        return node;
    }
}
