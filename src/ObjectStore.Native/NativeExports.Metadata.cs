using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for object metadata (per-object key-value pairs stored in a metadata block).
/// For Phase 7, metadata is stored as a simple key=value dictionary serialized to the object's metadata block.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_metadata_set")]
    public static unsafe int MetadataSet(IntPtr storeHandle, ulong objectId, byte* keyUtf8, byte* valueUtf8)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || keyUtf8 == null || valueUtf8 == null)
                return NativeErrorCodes.InvalidArg;

            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            string key = Marshal.PtrToStringUTF8((IntPtr)keyUtf8)!;
            string value = Marshal.PtrToStringUTF8((IntPtr)valueUtf8)!;

            engine.SetMetadata(objectId, key, value);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_metadata_get")]
    public static unsafe int MetadataGet(IntPtr storeHandle, ulong objectId, byte* keyUtf8,
                                          byte* outBuffer, int bufferLen, int* outLen)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || keyUtf8 == null || outLen == null)
                return NativeErrorCodes.InvalidArg;

            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            string key = Marshal.PtrToStringUTF8((IntPtr)keyUtf8)!;
            string? value = engine.GetMetadata(objectId, key);
            if (value == null)
            {
                *outLen = 0;
                return NativeErrorCodes.NotFound;
            }

            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
            *outLen = utf8.Length;

            if (outBuffer == null || bufferLen == 0)
            {
                // Two-call pattern: caller wants length only
                NativeErrorHelper.ClearLastError();
                return NativeErrorCodes.Ok;
            }

            if (bufferLen < utf8.Length)
                return NativeErrorCodes.BufferTooSmall;

            utf8.CopyTo(new Span<byte>(outBuffer, bufferLen));
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_metadata_delete")]
    public static unsafe int MetadataDelete(IntPtr storeHandle, ulong objectId, byte* keyUtf8)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || keyUtf8 == null)
                return NativeErrorCodes.InvalidArg;

            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            string key = Marshal.PtrToStringUTF8((IntPtr)keyUtf8)!;
            bool deleted = engine.DeleteMetadata(objectId, key);
            if (!deleted) return NativeErrorCodes.NotFound;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
