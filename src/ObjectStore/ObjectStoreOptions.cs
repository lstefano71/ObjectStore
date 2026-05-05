namespace ObjectStore;

/// <summary>
/// Configuration options for opening or creating an ObjectStore.
/// </summary>
public sealed class ObjectStoreOptions
{
    /// <summary>Cache capacity in bytes (0 = disabled).</summary>
    public long CacheMaxBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Default compression codec for new objects (0=None, 1=Deflate, 2=Brotli).</summary>
    public byte DefaultCompressionCodec { get; set; }

    /// <summary>Encryption key (32 bytes for AES-256-GCM). Null = no encryption.</summary>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>Open in read-only mode (no writes allowed).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// When true, reads automatically check whether another process has committed
    /// new data and refresh before returning. Required for multi-process readers
    /// to see writes from other processes without manually calling Refresh().
    /// Adds ~1μs per read for the generation check. Default: false.
    /// </summary>
    public bool MultiProcessMode { get; set; }

    /// <summary>
    /// Controls when block checksum validation is performed during reads.
    /// Default: Always (validate on every disk read, safest).
    /// </summary>
    public ChecksumPolicy ChecksumPolicy { get; set; } = ChecksumPolicy.Always;
}
