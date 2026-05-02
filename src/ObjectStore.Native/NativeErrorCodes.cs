namespace ObjectStore.Native;

/// <summary>
/// C API error codes matching the OBJSTORE_* constants in objstore.h.
/// </summary>
public static class NativeErrorCodes
{
    public const int Ok = 0;
    public const int Err = -1;
    public const int NotFound = -2;
    public const int Exists = -3;
    public const int ReadOnly = -4;
    public const int InvalidArg = -5;
    public const int BufferTooSmall = -6;
    public const int NoMoreItems = -7;
    public const int Timeout = -8;
    public const int Lock = -9;
    public const int Version = -10;
    public const int Corrupt = -11;
    public const int TxnAborted = -12;
    public const int NameNotFound = -13;
}
