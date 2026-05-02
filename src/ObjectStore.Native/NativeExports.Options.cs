using System.Runtime.InteropServices;

namespace ObjectStore.Native;

public static partial class NativeExports
{
    // ============================================================
    // Options handle
    // ============================================================

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_create")]
    public static unsafe int OptionsCreate(IntPtr* outOpts)
    {
        try
        {
            if (outOpts == null)
                return NativeErrorCodes.InvalidArg;

            var opts = new NativeOptions();
            *outOpts = HandleStore.Allocate(opts);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_set_read_only")]
    public static int OptionsSetReadOnly(IntPtr opts, int value)
    {
        try
        {
            var o = HandleStore.Get<NativeOptions>(opts);
            if (o == null) return NativeErrorCodes.InvalidArg;
            o.ReadOnly = value != 0;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_set_shared_access")]
    public static int OptionsSetSharedAccess(IntPtr opts, int value)
    {
        try
        {
            var o = HandleStore.Get<NativeOptions>(opts);
            if (o == null) return NativeErrorCodes.InvalidArg;
            o.SharedAccess = value != 0;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_set_cache_max_bytes")]
    public static int OptionsSetCacheMaxBytes(IntPtr opts, long value)
    {
        try
        {
            var o = HandleStore.Get<NativeOptions>(opts);
            if (o == null) return NativeErrorCodes.InvalidArg;
            o.CacheMaxBytes = value;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_set_lock_timeout_ms")]
    public static int OptionsSetLockTimeoutMs(IntPtr opts, int value)
    {
        try
        {
            var o = HandleStore.Get<NativeOptions>(opts);
            if (o == null) return NativeErrorCodes.InvalidArg;
            o.LockTimeoutMs = value;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_set_encryption_key")]
    public static unsafe int OptionsSetEncryptionKey(IntPtr opts, byte* key, nuint keyLen)
    {
        try
        {
            var o = HandleStore.Get<NativeOptions>(opts);
            if (o == null) return NativeErrorCodes.InvalidArg;

            if (key == null || keyLen == 0)
            {
                o.EncryptionKey = null;
            }
            else
            {
                o.EncryptionKey = new ReadOnlySpan<byte>(key, (int)keyLen).ToArray();
            }
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_options_free")]
    public static void OptionsFree(IntPtr opts)
    {
        HandleStore.Free(opts);
    }
}
