using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for store lifecycle: create, open, close.
/// </summary>
public static partial class NativeExports
{
    /// <summary>Applies all relevant NativeOptions to an opened engine.</summary>
    private static void ApplyNativeOptions(ObjectEngine engine, NativeOptions? opts)
    {
        if (opts == null) return;

        if (opts.MultiProcessMode)
            engine.MultiProcessMode = true;

        if (opts.CacheMaxBytes > 0)
        {
            int blockCapacity = (int)(opts.CacheMaxBytes / FormatConstants.MinBlockSize);
            engine.File.SetCacheCapacity(Math.Max(16, blockCapacity));
        }
        else if (opts.CacheMaxBytes == 0)
        {
            engine.File.SetCacheCapacity(0);
        }

        if (opts.LockTimeoutMs > 0)
            engine.LockTimeout = TimeSpan.FromMilliseconds(opts.LockTimeoutMs);

        if (opts.ChecksumPolicy >= 0 && opts.ChecksumPolicy <= 3)
            engine.File.ChecksumPolicy = (ChecksumPolicy)opts.ChecksumPolicy;
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_create")]
    public static unsafe int StoreCreate(byte* pathUtf8, IntPtr options, IntPtr* outHandle)
    {
        try
        {
            if (pathUtf8 == null || outHandle == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var opts = options != IntPtr.Zero ? HandleStore.Get<NativeOptions>(options) : null;

            if (opts?.ReadOnly == true)
                return NativeErrorCodes.InvalidArg; // Cannot create in read-only mode

            var engine = ObjectEngine.Create(path);
            ApplyNativeOptions(engine, opts);
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
            var opts = options != IntPtr.Zero ? HandleStore.Get<NativeOptions>(options) : null;

            ObjectEngine engine;
            if (opts?.ReadOnly == true)
                engine = ObjectEngine.OpenReadOnly(path);
            else
                engine = ObjectEngine.Open(path);

            ApplyNativeOptions(engine, opts);
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
            var opts = options != IntPtr.Zero ? HandleStore.Get<NativeOptions>(options) : null;

            ObjectEngine engine;
            if (opts?.ReadOnly == true)
            {
                if (!System.IO.File.Exists(path))
                    return NativeErrorCodes.InvalidArg; // Cannot create in read-only mode
                engine = ObjectEngine.OpenReadOnly(path);
            }
            else
            {
                engine = ObjectEngine.OpenOrCreate(path);
            }

            ApplyNativeOptions(engine, opts);
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

    [UnmanagedCallersOnly(EntryPoint = "objstore_refresh")]
    public static int StoreRefresh(IntPtr handle)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(handle);
            if (engine == null) return NativeErrorCodes.InvalidArg;
            engine.Refresh();
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
