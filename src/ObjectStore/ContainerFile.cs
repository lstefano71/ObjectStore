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

    /// <summary>
    /// When non-null, block writes are buffered in memory instead of going to disk.
    /// Flushed to disk as a batch at commit time for dramatically lower I/O during transactions.
    /// </summary>
    private Dictionary<long, (byte[] Raw, int BlockSize)>? _pendingWrites;

    public long FileSize => _fileSize;
    public string Path { get; }
    public SieveCache<long, byte[]>? BlockCache => _blockCache;
    public bool IsBufferingWrites => _pendingWrites != null;

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
            // Another process may have extended the file since our last RefreshFileSize().
            // Re-read the actual size before concluding the offset is out of bounds.
            _fileSize = _stream.Length;
            if (offset + buffer.Length > _fileSize)
            {
                buffer.Clear();
                return;
            }
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
    /// WARNING: The returned array may be shared with the cache — do NOT mutate it.
    /// Use ReadBlockMutable if you need to modify the data.
    /// </summary>
    public byte[] ReadBlock(long address, int order)
    {
        if (_blockCache != null && _blockCache.TryGet(address, out var cached))
            return cached!;

        int blockSize = FormatConstants.BlockSizeForOrder(order);
        byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            ReadRaw(address, raw.AsSpan(0, blockSize));
            int payloadSize = BlockHeader.Validate(raw.AsSpan(0, blockSize));
            byte[] payload = new byte[payloadSize];
            raw.AsSpan(FormatConstants.BlockHeaderSize, payloadSize).CopyTo(payload);

            _blockCache?.Put(address, payload);
            return payload;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>
    /// Reads a block and returns a mutable buffer sized to full payload capacity.
    /// Use this when you need to modify the data (e.g., COW append/write-at).
    /// The returned buffer may be larger than the stored payload (zero-padded).
    /// </summary>
    public byte[] ReadBlockMutable(long address, int order)
    {
        byte[] shared = ReadBlock(address, order);
        int payloadCapacity = FormatConstants.BlockSizeForOrder(order) - FormatConstants.BlockHeaderSize;
        byte[] mutable = new byte[payloadCapacity];
        shared.CopyTo(mutable, 0);
        return mutable;
    }

    /// <summary>
    /// Reads a block by first peeking the block size from the header.
    /// Use when the order is not known in advance (e.g., dynamically-sized B-tree nodes).
    /// Returns (payload, order).
    /// </summary>
    public (byte[] Payload, int Order) ReadBlockAuto(long address)
    {
        if (_blockCache != null && _blockCache.TryGet(address, out var cached))
        {
            // For cached blocks we still need to return the order. Peek the header.
            Span<byte> hdr = stackalloc byte[4];
            ReadRaw(address, hdr);
            int cachedBlockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hdr);
            int cachedOrder = FormatConstants.OrderFromBlockSize(cachedBlockSize);
            return (cached!, cachedOrder);
        }

        // Peek the first 4 bytes to determine block size
        Span<byte> header = stackalloc byte[4];
        ReadRaw(address, header);
        int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        int order = FormatConstants.OrderFromBlockSize(blockSize);

        byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            ReadRaw(address, raw.AsSpan(0, blockSize));
            int payloadSize = BlockHeader.Validate(raw.AsSpan(0, blockSize));
            byte[] payload = new byte[payloadSize];
            raw.AsSpan(FormatConstants.BlockHeaderSize, payloadSize).CopyTo(payload);

            _blockCache?.Put(address, payload);
            return (payload, order);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>
    /// Reads a block by peeking the header to determine size. Returns payload only.
    /// Use when the order is not needed (e.g., B-tree node deserialization).
    /// </summary>
    public byte[] ReadBlockAutoPayload(long address)
    {
        // Check pending writes first (buffered during transaction)
        if (_pendingWrites != null && _pendingWrites.TryGetValue(address, out var pending))
        {
            int payloadSize = BlockHeader.Validate(pending.Raw.AsSpan(0, pending.BlockSize));
            byte[] payload = new byte[payloadSize];
            pending.Raw.AsSpan(FormatConstants.BlockHeaderSize, payloadSize).CopyTo(payload);
            return payload;
        }

        if (_blockCache != null && _blockCache.TryGet(address, out var cached))
            return cached!;

        // Peek the first 4 bytes to determine block size
        Span<byte> header = stackalloc byte[4];
        ReadRaw(address, header);
        int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);

        byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            ReadRaw(address, raw.AsSpan(0, blockSize));
            int payloadSize = BlockHeader.Validate(raw.AsSpan(0, blockSize));
            byte[] payload = new byte[payloadSize];
            raw.AsSpan(FormatConstants.BlockHeaderSize, payloadSize).CopyTo(payload);

            _blockCache?.Put(address, payload);
            return payload;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
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
        BlockHeader.WriteBlock(raw.AsSpan(0, blockSize), payload, flags);

        if (_pendingWrites != null)
        {
            _pendingWrites[address] = (raw, blockSize);
        }
        else
        {
            WriteRaw(address, raw.AsSpan(0, blockSize));
        }

        // Cache the actual payload (not the full block capacity — ReadBlock does the same)
        if (_blockCache != null)
        {
            byte[] cachedPayload = new byte[payload.Length];
            payload.CopyTo(cachedPayload);
            _blockCache.Put(address, cachedPayload);
        }
    }

    /// <summary>
    /// Writes a block and takes ownership of the payload array for caching (no copy).
    /// The caller must not use the array after this call.
    /// </summary>
    public void WriteBlockOwned(long address, int order, byte[] ownedPayload, byte flags = FormatConstants.BlockFlagInUse)
    {
        int blockSize = FormatConstants.BlockSizeForOrder(order);
        int payloadCapacity = blockSize - FormatConstants.BlockHeaderSize;

        if (ownedPayload.Length > payloadCapacity)
            throw new ArgumentException($"Payload ({ownedPayload.Length}) exceeds block capacity ({payloadCapacity}).");

        byte[] raw = new byte[blockSize];
        BlockHeader.WriteBlock(raw.AsSpan(0, blockSize), ownedPayload, flags);

        if (_pendingWrites != null)
        {
            _pendingWrites[address] = (raw, blockSize);
        }
        else
        {
            WriteRaw(address, raw.AsSpan(0, blockSize));
        }

        _blockCache?.Put(address, ownedPayload);
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

    /// <summary>Begins buffering block writes in memory instead of writing to disk.</summary>
    public void BeginBufferedWrites()
    {
        _pendingWrites ??= new Dictionary<long, (byte[] Raw, int BlockSize)>();
    }

    /// <summary>
    /// Writes all pending buffered blocks to disk. Does NOT flush to disk (caller does that).
    /// </summary>
    public void DrainBufferedWrites()
    {
        if (_pendingWrites == null || _pendingWrites.Count == 0) return;
        // Sort by address for sequential I/O (helps on HDD, reduces seeks)
        var sorted = _pendingWrites.OrderBy(kv => kv.Key);
        foreach (var (address, (raw, blockSize)) in sorted)
        {
            long needed = address + blockSize;
            if (needed > _fileSize) GrowTo(needed);
            _stream.Seek(address, SeekOrigin.Begin);
            _stream.Write(raw.AsSpan(0, blockSize));
        }
        _pendingWrites.Clear();
    }

    /// <summary>Discards all buffered writes (used on transaction rollback).</summary>
    public void DiscardBufferedWrites()
    {
        if (_pendingWrites != null)
        {
            // Invalidate cache entries for discarded writes
            if (_blockCache != null)
            {
                foreach (var addr in _pendingWrites.Keys)
                    _blockCache.Invalidate(addr);
            }
            _pendingWrites.Clear();
        }
    }

    /// <summary>Ends write buffering mode.</summary>
    public void EndBufferedWrites()
    {
        _pendingWrites = null;
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
