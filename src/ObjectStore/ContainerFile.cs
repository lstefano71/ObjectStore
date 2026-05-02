namespace ObjectStore;

/// <summary>
/// Wraps a FileStream to provide block-level I/O with automatic file growth.
/// </summary>
public sealed class ContainerFile : IDisposable
{
    private readonly FileStream _stream;
    private long _fileSize;

    // Growth policy: double up to 256 MB, then grow by 64 MB increments
    private const long DoubleThreshold = 256 * 1024 * 1024;
    private const long LinearGrowthIncrement = 64 * 1024 * 1024;

    public long FileSize => _fileSize;
    public string Path { get; }

    private ContainerFile(FileStream stream, string path)
    {
        _stream = stream;
        _fileSize = stream.Length;
        Path = path;
    }

    /// <summary>Creates a new container file. Fails if file already exists.</summary>
    public static ContainerFile Create(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.RandomAccess);
        return new ContainerFile(stream, path);
    }

    /// <summary>Opens an existing container file.</summary>
    public static ContainerFile Open(string path, bool readOnly = false)
    {
        var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        var share = readOnly ? FileShare.Read : FileShare.None;
        var stream = new FileStream(path, FileMode.Open, access, share, 4096, FileOptions.RandomAccess);
        return new ContainerFile(stream, path);
    }

    /// <summary>Opens or creates a container file.</summary>
    public static ContainerFile OpenOrCreate(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.RandomAccess);
        return new ContainerFile(stream, path);
    }

    /// <summary>Reads raw bytes at the specified file offset.</summary>
    public void ReadRaw(long offset, Span<byte> buffer)
    {
        if (offset + buffer.Length > _fileSize)
        {
            buffer.Clear();
            return;
        }
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);
    }

    /// <summary>Writes raw bytes at the specified file offset, growing the file if needed.</summary>
    public void WriteRaw(long offset, ReadOnlySpan<byte> data)
    {
        long needed = offset + data.Length;
        if (needed > _fileSize)
            GrowTo(needed);

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(data);
    }

    /// <summary>
    /// Reads a block at the given address. Validates the block header checksum.
    /// Returns the payload (without header).
    /// </summary>
    public byte[] ReadBlock(long address, int order)
    {
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        byte[] raw = new byte[blockSize];
        ReadRaw(address, raw);
        BlockHeader.ValidateAndGetPayload(raw, out var payload);
        return payload;
    }

    /// <summary>
    /// Writes a block at the given address. Prepends the block header with checksum.
    /// </summary>
    public void WriteBlock(long address, int order, ReadOnlySpan<byte> payload, byte flags = FormatConstants.BlockFlagInUse)
    {
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        int payloadCapacity = blockSize - FormatConstants.BlockHeaderSize;

        if (payload.Length > payloadCapacity)
            throw new ArgumentException($"Payload ({payload.Length}) exceeds block capacity ({payloadCapacity}).");

        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw, payload, flags);
        WriteRaw(address, raw);
    }

    /// <summary>Ensures the file is at least targetSize bytes.</summary>
    public void EnsureSize(long targetSize)
    {
        if (targetSize > _fileSize)
            GrowTo(targetSize);
    }

    /// <summary>Flushes all buffered data to disk.</summary>
    public void Flush()
    {
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void GrowTo(long minSize)
    {
        long newSize = _fileSize;
        if (newSize == 0) newSize = FormatConstants.DataRegionOffset;

        while (newSize < minSize)
        {
            if (newSize < DoubleThreshold)
                newSize = Math.Max(newSize * 2, FormatConstants.DataRegionOffset);
            else
                newSize += LinearGrowthIncrement;
        }

        _stream.SetLength(newSize);
        _fileSize = newSize;
    }
}
