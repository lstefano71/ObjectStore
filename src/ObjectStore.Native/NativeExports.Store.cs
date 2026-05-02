using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for store lifecycle: create, open, close.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_create")]
    public static unsafe int StoreCreate(byte* pathUtf8, IntPtr options, IntPtr* outHandle)
    {
        try
        {
            if (pathUtf8 == null || outHandle == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var engine = ObjectEngine.Create(path);
            *outHandle = HandleStore.Allocate(engine);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_open")]
    public static unsafe int StoreOpen(byte* pathUtf8, IntPtr options, IntPtr* outHandle)
    {
        try
        {
            if (pathUtf8 == null || outHandle == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var engine = ObjectEngine.Open(path);
            *outHandle = HandleStore.Allocate(engine);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_open_or_create")]
    public static unsafe int StoreOpenOrCreate(byte* pathUtf8, IntPtr options, IntPtr* outHandle)
    {
        try
        {
            if (pathUtf8 == null || outHandle == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var engine = ObjectEngine.OpenOrCreate(path);
            *outHandle = HandleStore.Allocate(engine);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_close")]
    public static int StoreClose(IntPtr handle)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(handle);
            if (engine == null) return NativeErrorCodes.InvalidArg;
            engine.Dispose();
            HandleStore.Free(handle);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_open_readonly")]
    public static unsafe int StoreOpenReadOnly(byte* pathUtf8, IntPtr options, IntPtr* outHandle)
    {
        try
        {
            if (pathUtf8 == null || outHandle == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var engine = ObjectEngine.OpenReadOnly(path);
            *outHandle = HandleStore.Allocate(engine);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
