namespace ObjectStore;

/// <summary>
/// Public information about a stored object.
/// </summary>
public sealed class ObjectInfo
{
    public ulong Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public byte CompressionCodec { get; init; }

    internal static ObjectInfo FromRecord(NodeRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Size = record.Size,
        Created = DateTimeOffset.FromUnixTimeMilliseconds(record.Created),
        Modified = DateTimeOffset.FromUnixTimeMilliseconds(record.Modified),
        CompressionCodec = record.CompressionCodec,
    };
}
