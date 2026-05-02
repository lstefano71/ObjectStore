using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// Iterator state for listing objects.
/// </summary>
internal sealed class NativeIterator
{
    public IEnumerator<NodeRecord> Enumerator { get; }
    public bool HasCurrent { get; set; }

    public NativeIterator(IEnumerable<NodeRecord> source)
    {
        Enumerator = source.GetEnumerator();
        HasCurrent = false;
    }
}

/// <summary>
/// C API exports for iterator-based object listing.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_list_begin")]
    public static unsafe int ListBegin(IntPtr storeHandle, IntPtr* outIter)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || outIter == null)
                return NativeErrorCodes.InvalidArg;

            var iter = new NativeIterator(engine.ListObjects());
            *outIter = HandleStore.Allocate(iter);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_iter_next")]
    public static unsafe int IterNext(IntPtr iterHandle, ulong* outId, long* outSize)
    {
        try
        {
            var iter = HandleStore.Get<NativeIterator>(iterHandle);
            if (iter == null)
                return NativeErrorCodes.InvalidArg;

            if (!iter.Enumerator.MoveNext())
            {
                // No more items
                if (outId != null) *outId = 0;
                if (outSize != null) *outSize = 0;
                return 1; // 1 = end of iteration
            }

            var record = iter.Enumerator.Current;
            if (outId != null) *outId = record.Id;
            if (outSize != null) *outSize = record.Size;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_iter_close")]
    public static int IterClose(IntPtr iterHandle)
    {
        try
        {
            var iter = HandleStore.Get<NativeIterator>(iterHandle);
            if (iter == null) return NativeErrorCodes.InvalidArg;
            iter.Enumerator.Dispose();
            HandleStore.Free(iterHandle);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_get_stats")]
    public static unsafe int GetStats(IntPtr storeHandle, int* outObjectCount, long* outTotalSize, long* outFileSize)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            int count = 0;
            long totalSize = 0;
            foreach (var record in engine.ListObjects())
            {
                count++;
                totalSize += record.Size;
            }

            if (outObjectCount != null) *outObjectCount = count;
            if (outTotalSize != null) *outTotalSize = totalSize;
            if (outFileSize != null) *outFileSize = engine.File.FileSize;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_defragment")]
    public static unsafe int DoDefragment(IntPtr storeHandle, int* outCount)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            int count = Defragmenter.Defragment(engine);
            if (outCount != null) *outCount = count;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_recover")]
    public static unsafe int DoRecover(IntPtr storeHandle, int* outNeeded)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            bool needed = Recovery.Recover(engine);
            if (outNeeded != null) *outNeeded = needed ? 1 : 0;

            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
