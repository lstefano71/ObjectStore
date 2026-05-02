using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// GCHandle-based opaque handle store for managing managed objects across the FFI boundary.
/// </summary>
public static class HandleStore
{
    public static IntPtr Allocate<T>(T obj) where T : class
    {
        var handle = GCHandle.Alloc(obj, GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    public static T? Get<T>(IntPtr ptr) where T : class
    {
        if (ptr == IntPtr.Zero) return null;
        var handle = GCHandle.FromIntPtr(ptr);
        return handle.Target as T;
    }

    public static void Free(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ptr);
        handle.Free();
    }
}

/// <summary>
/// Managed backing type for the objstore_options_t opaque handle.
/// </summary>
public sealed class NativeOptions
{
    public bool ReadOnly { get; set; }
    public bool SharedAccess { get; set; }
    public bool MultiProcessMode { get; set; }
    public long CacheMaxBytes { get; set; } = 32 * 1024 * 1024;
    public int LockTimeoutMs { get; set; } = 30_000;
    public byte[]? EncryptionKey { get; set; }
    public byte CompressionCodec { get; set; } // 0=None, 1=Deflate, 2=Brotli
}
