namespace ObjectStore;

/// <summary>
/// Public-facing ObjectStore database. Wraps ObjectEngine with a user-friendly API.
/// </summary>
public sealed class ObjectStoreDatabase : IDisposable
{
    private readonly ObjectEngine _engine;
    private readonly ObjectStoreOptions _options;
    private bool _disposed;

    private ObjectStoreDatabase(ObjectEngine engine, ObjectStoreOptions options)
    {
        _engine = engine;
        _options = options;
    }

    /// <summary>Creates a new object store file.</summary>
    public static ObjectStoreDatabase Create(string path, ObjectStoreOptions? options = null)
    {
        options ??= new ObjectStoreOptions();
        var engine = ObjectEngine.Create(path);
        ApplyOptions(engine, options);
        return new ObjectStoreDatabase(engine, options);
    }

    /// <summary>Opens an existing object store file.</summary>
    public static ObjectStoreDatabase Open(string path, ObjectStoreOptions? options = null)
    {
        options ??= new ObjectStoreOptions();
        var engine = ObjectEngine.Open(path);
        ApplyOptions(engine, options);
        return new ObjectStoreDatabase(engine, options);
    }

    /// <summary>Opens an existing file or creates a new one.</summary>
    public static ObjectStoreDatabase OpenOrCreate(string path, ObjectStoreOptions? options = null)
    {
        options ??= new ObjectStoreOptions();
        var engine = ObjectEngine.OpenOrCreate(path);
        ApplyOptions(engine, options);
        return new ObjectStoreDatabase(engine, options);
    }

    private static void ApplyOptions(ObjectEngine engine, ObjectStoreOptions options)
    {
        if (options.CacheMaxBytes > 0)
        {
            int blockCapacity = (int)(options.CacheMaxBytes / FormatConstants.MinBlockSize);
            engine.File.SetCacheCapacity(Math.Max(16, blockCapacity));
        }
        engine.MultiProcessMode = options.MultiProcessMode;
    }

    // --- Object Operations ---

    /// <summary>Creates a new object with an optional name. Returns its ID.</summary>
    public ulong CreateObject(string? name = null)
    {
        ThrowIfDisposed();
        return _engine.CreateObject(name, _options.DefaultCompressionCodec);
    }

    /// <summary>Deletes an object by ID. Returns true if found and deleted.</summary>
    public bool DeleteObject(ulong id)
    {
        ThrowIfDisposed();
        return _engine.DeleteObject(id);
    }

    /// <summary>Checks if an object exists.</summary>
    public bool Exists(ulong id)
    {
        ThrowIfDisposed();
        return _engine.Exists(id);
    }

    /// <summary>Gets object info or null if not found.</summary>
    public ObjectInfo? GetInfo(ulong id)
    {
        ThrowIfDisposed();
        var record = _engine.GetInfo(id);
        return record != null ? ObjectInfo.FromRecord(record) : null;
    }

    /// <summary>Lists all objects in the store.</summary>
    public IEnumerable<ObjectInfo> ListObjects()
    {
        ThrowIfDisposed();
        return _engine.ListObjects().Select(ObjectInfo.FromRecord);
    }

    // --- Data I/O ---

    /// <summary>Appends data to an object.</summary>
    public void Append(ulong id, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        _engine.Append(id, data);
    }

    /// <summary>Reads data from an object at a given offset.</summary>
    public int ReadAt(ulong id, long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        return _engine.ReadAt(id, offset, buffer);
    }

    /// <summary>Overwrites data at a specific offset (cannot extend).</summary>
    public void WriteAt(ulong id, long offset, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        _engine.WriteAt(id, offset, data);
    }

    /// <summary>Truncates an object to the specified length.</summary>
    public void Truncate(ulong id, long newLength)
    {
        ThrowIfDisposed();
        _engine.Truncate(id, newLength);
    }

    // --- Transactions ---

    /// <summary>Begins an explicit transaction (supports nesting via savepoints).</summary>
    public void BeginTransaction()
    {
        ThrowIfDisposed();
        _engine.BeginTransaction();
    }

    /// <summary>Commits the current transaction.</summary>
    public void CommitTransaction()
    {
        ThrowIfDisposed();
        _engine.CommitTransaction();
    }

    /// <summary>Rolls back the current transaction.</summary>
    public void RollbackTransaction()
    {
        ThrowIfDisposed();
        _engine.RollbackTransaction();
    }

    // --- Statistics ---

    /// <summary>Gets container statistics.</summary>
    public StoreStats GetStats()
    {
        ThrowIfDisposed();
        int objectCount = 0;
        long totalDataSize = 0;
        foreach (var record in _engine.ListObjects())
        {
            objectCount++;
            totalDataSize += record.Size;
        }
        return new StoreStats
        {
            ObjectCount = objectCount,
            TotalDataSize = totalDataSize,
            ContainerFileSize = _engine.File.FileSize,
        };
    }

    // --- Maintenance ---

    /// <summary>
    /// Recovers the store after a dirty close by scanning reachable blocks
    /// and rebuilding the allocator. Returns true if recovery was needed.
    /// </summary>
    public bool Recover()
    {
        ThrowIfDisposed();
        return Recovery.Recover(_engine);
    }

    /// <summary>
    /// Defragments the store by rewriting object data contiguously.
    /// Returns the number of objects defragmented.
    /// </summary>
    public int Defragment()
    {
        ThrowIfDisposed();
        return Defragmenter.Defragment(_engine);
    }

    /// <summary>Access to the underlying engine (for advanced/test scenarios).</summary>
    internal ObjectEngine Engine => _engine;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _engine.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>Container statistics.</summary>
public sealed class StoreStats
{
    public int ObjectCount { get; init; }
    public long TotalDataSize { get; init; }
    public long ContainerFileSize { get; init; }
}
