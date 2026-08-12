using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal static unsafe partial class NativeDawnMethods
{
    internal const string LibraryName = "progpu_native_dawn";
    internal const uint AdapterAbiVersion = 1;
    internal const uint RequiredProviderAbiVersion = 2;

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetNativeAbiVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetInfo(NativeMethods.EngineInfo* info);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_dawn_get_adapter_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAdapterAbiVersion();
}
