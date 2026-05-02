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
}
