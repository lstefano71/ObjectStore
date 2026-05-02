using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for object CRUD and data I/O.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_object_create")]
    public static unsafe int ObjectCreate(IntPtr storeHandle, byte* nameUtf8, ulong* outId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;
            if (outId == null) return NativeErrorCodes.InvalidArg;

            string? name = nameUtf8 != null ? Marshal.PtrToStringUTF8((IntPtr)nameUtf8) : null;
            ulong id = engine.CreateObject(name);
            *outId = id;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_object_delete")]
    public static int ObjectDelete(IntPtr storeHandle, ulong objectId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            bool found = engine.DeleteObject(objectId);
            if (!found) return NativeErrorCodes.NotFound;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_object_exists")]
    public static unsafe int ObjectExists(IntPtr storeHandle, ulong objectId, int* outExists)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || outExists == null) return NativeErrorCodes.InvalidArg;

            *outExists = engine.Exists(objectId) ? 1 : 0;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_object_get_size")]
    public static unsafe int ObjectGetSize(IntPtr storeHandle, ulong objectId, long* outSize)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || outSize == null) return NativeErrorCodes.InvalidArg;

            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            *outSize = info.Size;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_write")]
    public static unsafe int Write(IntPtr storeHandle, ulong objectId, long offset, byte* data, int length)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || data == null) return NativeErrorCodes.InvalidArg;
            if (length < 0) return NativeErrorCodes.InvalidArg;

            var span = new ReadOnlySpan<byte>(data, length);
            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            if (offset > info.Size)
            {
                // Gap write — not allowed
                return NativeErrorCodes.InvalidArg;
            }
            else if (offset == info.Size)
            {
                // Append at end
                engine.Append(objectId, span);
            }
            else if (offset + length <= info.Size)
            {
                // Overwrite existing region
                engine.WriteAt(objectId, offset, span);
            }
            else
            {
                // Partial overwrite + extend: write what fits, append the rest
                int overwriteLen = (int)(info.Size - offset);
                if (overwriteLen > 0)
                    engine.WriteAt(objectId, offset, span[..overwriteLen]);
                engine.Append(objectId, span[overwriteLen..]);
            }

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_append")]
    public static unsafe int Append(IntPtr storeHandle, ulong objectId, byte* data, int length)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || data == null) return NativeErrorCodes.InvalidArg;
            if (length < 0) return NativeErrorCodes.InvalidArg;

            var span = new ReadOnlySpan<byte>(data, length);
            engine.Append(objectId, span);

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_read")]
    public static unsafe int Read(IntPtr storeHandle, ulong objectId, long offset, byte* buffer, int bufferLen, int* outBytesRead)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || buffer == null || outBytesRead == null)
                return NativeErrorCodes.InvalidArg;
            if (bufferLen < 0) return NativeErrorCodes.InvalidArg;

            var info = engine.GetInfo(objectId);
            if (info == null) return NativeErrorCodes.NotFound;

            var span = new Span<byte>(buffer, bufferLen);
            int bytesRead = engine.ReadAt(objectId, offset, span);
            *outBytesRead = bytesRead;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_truncate")]
    public static int Truncate(IntPtr storeHandle, ulong objectId, long newLength)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;
            if (newLength < 0) return NativeErrorCodes.InvalidArg;

            engine.Truncate(objectId, newLength);

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
