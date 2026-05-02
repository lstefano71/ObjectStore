using System.Runtime.InteropServices;

namespace ObjectStore.Native;

/// <summary>
/// Entry points exported as C ABI functions via NativeAOT.
/// </summary>
public static partial class NativeExports
{
    private const int ApiVersionMajor = 1;
    private const int ApiVersionMinor = 0;

    [UnmanagedCallersOnly(EntryPoint = "objstore_get_version")]
    public static unsafe int GetVersion(int* outMajor, int* outMinor)
    {
        try
        {
            if (outMajor == null || outMinor == null)
                return NativeErrorCodes.InvalidArg;

            *outMajor = ApiVersionMajor;
            *outMinor = ApiVersionMinor;
            return NativeErrorCodes.Ok;
        }
        catch (Exception ex)
        {
            return NativeErrorHelper.Capture(ex);
        }
    }
}
