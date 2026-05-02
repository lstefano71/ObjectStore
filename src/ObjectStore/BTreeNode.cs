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
        // Pre-calculate total size
        int size = 4; // header: nodeType(1) + keyCount(2) + reserved(1)
        size += Keys.Count * BTreeKey.Size;

        if (IsLeaf)
        {
            foreach (var value in Values)
                size += 2 + value.Length; // u16 length prefix + data
        }
        else
        {
            size += (Keys.Count + 1) * 8; // child pointers
        }

        byte[] buffer = new byte[size];
        var span = buffer.AsSpan();
        int offset = 0;

        // Header
        span[offset++] = NodeType;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)Keys.Count);
        offset += 2;
        span[offset++] = 0; // reserved

        // Keys
        foreach (var key in Keys)
        {
            key.WriteTo(span.Slice(offset, BTreeKey.Size));
            offset += BTreeKey.Size;
        }

        if (IsLeaf)
        {
            // Values: length-prefixed byte arrays
            foreach (var value in Values)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)value.Length);
                offset += 2;
                value.CopyTo(span[offset..]);
                offset += value.Length;
            }
        }
        else
        {
            // Children: (keyCount + 1) × u64
            foreach (var child in Children)
            {
                BinaryPrimitives.WriteInt64LittleEndian(span[offset..], child);
                offset += 8;
            }
        }

        return buffer;
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
