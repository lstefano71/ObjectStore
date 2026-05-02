using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// C API exports for node hierarchy operations.
/// </summary>
public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "objstore_create_child")]
    public static unsafe int CreateChild(IntPtr storeHandle, ulong parentId, byte* nameUtf8, int isContainer, ulong* outId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || nameUtf8 == null || outId == null)
                return NativeErrorCodes.InvalidArg;

            string name = Marshal.PtrToStringUTF8((IntPtr)nameUtf8)!;
            ulong id = engine.CreateChild(parentId, name, isContainer != 0);
            *outId = id;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_move_node")]
    public static unsafe int MoveNode(IntPtr storeHandle, ulong nodeId, ulong newParentId, byte* newNameUtf8)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            string? newName = newNameUtf8 != null ? Marshal.PtrToStringUTF8((IntPtr)newNameUtf8) : null;
            engine.MoveNode(nodeId, newParentId, newName);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_delete_subtree")]
    public static int DeleteSubtree(IntPtr storeHandle, ulong nodeId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null) return NativeErrorCodes.InvalidArg;

            engine.DeleteSubtree(nodeId);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_resolve_path")]
    public static unsafe int ResolvePath(IntPtr storeHandle, byte* pathUtf8, ulong* outId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || pathUtf8 == null || outId == null)
                return NativeErrorCodes.InvalidArg;

            string path = Marshal.PtrToStringUTF8((IntPtr)pathUtf8)!;
            var id = engine.ResolvePath(path);
            if (id == null) return NativeErrorCodes.NotFound;

            *outId = id.Value;
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_list_children_begin")]
    public static unsafe int ListChildrenBegin(IntPtr storeHandle, ulong parentId, IntPtr* outIter)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || outIter == null)
                return NativeErrorCodes.InvalidArg;

            var iter = new NativeIterator(engine.ListChildren(parentId));
            *outIter = HandleStore.Allocate(iter);
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "objstore_get_root_id")]
    public static unsafe int GetRootId(IntPtr storeHandle, ulong* outId)
    {
        try
        {
            var engine = HandleStore.Get<ObjectEngine>(storeHandle);
            if (engine == null || outId == null)
                return NativeErrorCodes.InvalidArg;

            *outId = 1; // Root is always ID 1
            NativeErrorHelper.ClearLastError();
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
