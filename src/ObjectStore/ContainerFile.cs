namespace ObjectStore;

/// <summary>
/// Wraps a FileStream to provide block-level I/O with automatic file growth
/// and an integrated SIEVE block cache.
/// </summary>
public sealed class ContainerFile : IDisposable
{
    private readonly FileStream _stream;
    private long _fileSize;
    private SieveCache<long, byte[]>? _blockCache;

    // Growth policy: double up to 256 MB, then grow by 64 MB increments
    private const long DoubleThreshold = 256 * 1024 * 1024;
    private const long LinearGrowthIncrement = 64 * 1024 * 1024;
    private const int DefaultCacheCapacity = 1024; // default block cache entries

    public long FileSize => _fileSize;
    public string Path { get; }
    public SieveCache<long, byte[]>? BlockCache => _blockCache;

    private ContainerFile(FileStream stream, string path)
    {
        _stream = stream;
        _fileSize = stream.Length;
        Path = path;
        _blockCache = new SieveCache<long, byte[]>(DefaultCacheCapacity);
    }

    /// <summary>Creates a new container file. Fails if file already exists.</summary>
    public static ContainerFile Create(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite, bufferSize: 1, FileOptions.RandomAccess);
        return new ContainerFile(stream, path);
    }

    /// <summary>Opens an existing container file.</summary>
    public static ContainerFile Open(string path, bool readOnly = false)
    {
        var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        var stream = new FileStream(path, FileMode.Open, access, FileShare.ReadWrite, bufferSize: 1, FileOptions.RandomAccess);
        return new ContainerFile(stream, path);
    }

    /// <summary>Opens or creates a container file.</summary>
    public static ContainerFile OpenOrCreate(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite, bufferSize: 1, FileOptions.RandomAccess);
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
    /// Reads a block at the given address. Uses cache if available.
    /// Validates the block header checksum. Returns the payload (without header).
    /// </summary>
    public byte[] ReadBlock(long address, int order)
    {
        if (_blockCache != null && _blockCache.TryGet(address, out var cached))
            return (byte[])cached!.Clone(); // Return a copy to prevent mutation of cache

        int blockSize = FormatConstants.BlockSizeForOrder(order);
        byte[] raw = new byte[blockSize];
        ReadRaw(address, raw);
        BlockHeader.ValidateAndGetPayload(raw, out var payload);

        _blockCache?.Put(address, payload);
        return (byte[])payload.Clone();
    }

    /// <summary>
    /// Writes a block at the given address. Prepends the block header with checksum.
    /// Invalidates and updates the cache.
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

        // Cache the full payload area (zero-padded) to match what ReadBlock returns
        if (_blockCache != null)
        {
            byte[] cachedPayload = new byte[payloadCapacity];
            payload.CopyTo(cachedPayload);
            _blockCache.Put(address, cachedPayload);
        }
    }

    /// <summary>Invalidates a cached block (e.g., when freed).</summary>
    public void InvalidateBlock(long address)
    {
        _blockCache?.Invalidate(address);
    }

    /// <summary>Sets the block cache capacity. Pass 0 to disable caching.</summary>
    public void SetCacheCapacity(int capacity)
    {
        if (capacity <= 0)
            _blockCache = null;
        else
            _blockCache = new SieveCache<long, byte[]>(capacity);
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

    // Lock sentinel offset — far beyond any real file data to avoid blocking reads.
    private const long LockOffset = long.MaxValue - 1;

    /// <summary>
    /// Acquires an exclusive byte-range lock for cross-process write serialization.
    /// Blocks until the lock is acquired or timeout expires.
    /// </summary>
    public void AcquireWriteLock(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                _stream.Lock(LockOffset, 1);
                return;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new LockTimeoutException(
                        $"Could not acquire write lock within {timeout.TotalMilliseconds}ms.");
                Thread.Sleep(1); // Brief yield before retry
            }
        }
    }

    /// <summary>Releases the exclusive byte-range write lock.</summary>
    public void ReleaseWriteLock()
    {
        _stream.Unlock(LockOffset, 1);
    }

    /// <summary>Re-reads the file length from the OS (another process may have grown the file).</summary>
    public void RefreshFileSize()
    {
        _fileSize = _stream.Length;
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
