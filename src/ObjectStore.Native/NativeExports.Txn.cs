using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for transaction management.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_txn_begin")]
    public static int TxnBegin(IntPtr storeHandle)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            engine.BeginTransaction();
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_txn_commit")]
    public static int TxnCommit(IntPtr storeHandle)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            engine.CommitTransaction();
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_txn_rollback")]
    public static int TxnRollback(IntPtr storeHandle)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            engine.RollbackTransaction();
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
