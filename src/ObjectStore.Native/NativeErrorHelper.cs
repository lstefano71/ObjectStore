namespace ObjectStore.Native;

/// <summary>
/// Captures exceptions at the FFI boundary and maps them to error codes.
/// Stores the last error message in a thread-static slot.
/// </summary>
public static class NativeErrorHelper
{
    [ThreadStatic]
    private static string? _lastErrorDetail;

    /// <summary>
    /// Maps an exception to an error code and stores the message for later retrieval.
    /// </summary>
    public static int Capture(Exception ex)
    {
        _lastErrorDetail = ex.Message;

        return ex switch
        {
            ObjectNotFoundException => NativeErrorCodes.NotFound,
            ObjectAlreadyExistsException => NativeErrorCodes.Exists,
            ReadOnlyContainerException => NativeErrorCodes.ReadOnly,
            BlockCorruptedException => NativeErrorCodes.Corrupt,
            IncompatibleVersionException => NativeErrorCodes.Version,
            OperationCanceledException => NativeErrorCodes.Timeout,
            ArgumentNullException => NativeErrorCodes.InvalidArg,
            ArgumentException => NativeErrorCodes.InvalidArg,
            _ => NativeErrorCodes.Err,
        };
    }

    /// <summary>Gets the last error message for the current thread.</summary>
    public static string? GetLastErrorDetail() => _lastErrorDetail;

    /// <summary>Clears the last error (called on successful operations).</summary>
    public static void ClearLastError() => _lastErrorDetail = null;
}
