using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Describes the separately packaged provider-resolved Dawn renderer.
/// </summary>
/// <remarks>
/// This type verifies the native and adapter ABI without loading or owning a
/// WebScene provider. Provider/device/canvas ownership is exposed by the typed
/// compositor integration only after a compatible provider is supplied.
/// </remarks>
public static unsafe class NativeDawnAdapter
{
    public const uint AdapterAbiVersion = 1;
    public const uint RequiredProviderAbiVersion = 2;
    public const uint BackendAbi = 2;

    public static NativeRendererInfo GetInfo()
    {
        if (NativeDawnMethods.GetNativeAbiVersion() != NativeMethods.AbiVersion)
        {
            throw new NotSupportedException(
                "The loaded ProGPU Dawn adapter has an incompatible native ABI.");
        }
        if (NativeDawnMethods.GetAdapterAbiVersion() != AdapterAbiVersion)
        {
            throw new NotSupportedException(
                "The loaded ProGPU Dawn adapter has an incompatible adapter ABI.");
        }

        var info = new NativeMethods.EngineInfo
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineInfo>()
        };
        if (NativeDawnMethods.GetInfo(&info) == 0 ||
            info.BackendAbi != BackendAbi)
        {
            throw new InvalidOperationException(
                "The ProGPU Dawn adapter did not return its expected backend identity.");
        }

        byte* namePointer = info.Name;
        string name = Marshal.PtrToStringUTF8((nint)namePointer) ?? string.Empty;
        return new NativeRendererInfo(
            info.AbiVersion,
            info.BackendAbi,
            (NativeRendererCapabilities)info.Capabilities,
            name);
    }
}
