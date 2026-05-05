namespace ObjectStore;

/// <summary>
/// Controls when block checksum validation is performed during reads.
/// </summary>
public enum ChecksumPolicy
{
    /// <summary>
    /// Validate checksum on every disk read (skip if block is in cache).
    /// This is the safest mode and the default.
    /// </summary>
    Always = 0,

    /// <summary>
    /// Validate checksums only for metadata blocks (B-tree nodes, extent lists,
    /// buddy state, object metadata). Skip validation for object data extent blocks.
    /// Good balance of safety and performance for read-heavy workloads.
    /// </summary>
    MetadataOnly = 1,

    /// <summary>
    /// Validate checksum on the first disk read of each block address.
    /// Subsequent reads of the same address (even after cache eviction) skip validation.
    /// Reset on Refresh() to handle external writes.
    /// </summary>
    OnFirstRead = 2,

    /// <summary>
    /// Never validate block checksums on read. Fastest mode but provides no
    /// corruption detection. Use only when integrity is guaranteed by other means.
    /// </summary>
    None = 3,
}
