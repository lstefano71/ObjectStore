namespace ObjectStore;

/// <summary>
/// Thrown when a block's checksum validation fails, indicating data corruption.
/// </summary>
public class BlockCorruptedException : Exception
{
    public long BlockAddress { get; }

    public BlockCorruptedException(long blockAddress, string message)
        : base(message)
    {
        BlockAddress = blockAddress;
    }
}

/// <summary>
/// Thrown when attempting to open a container with an incompatible format version.
/// </summary>
public class IncompatibleVersionException : Exception
{
    public ushort FileMajor { get; }
    public ushort FileMinor { get; }

    public IncompatibleVersionException(ushort major, ushort minor)
        : base($"Incompatible container version {major}.{minor}; this library supports {FormatConstants.VersionMajor}.x")
    {
        FileMajor = major;
        FileMinor = minor;
    }
}

/// <summary>
/// Thrown when attempting a write operation on a read-only store.
/// </summary>
public class ReadOnlyContainerException : Exception
{
    public ReadOnlyContainerException()
        : base("Cannot perform write operations on a read-only store.") { }
}

/// <summary>
/// Thrown when a referenced object is not found.
/// </summary>
public class ObjectNotFoundException : Exception
{
    public ObjectNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Thrown when creating an object that already exists.
/// </summary>
public class ObjectAlreadyExistsException : Exception
{
    public ObjectAlreadyExistsException(string message) : base(message) { }
}

/// <summary>
/// Thrown when the write lock cannot be acquired within the configured timeout.
/// </summary>
public class LockTimeoutException : Exception
{
    public LockTimeoutException(string message) : base(message) { }
}
