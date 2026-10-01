using System.Runtime.InteropServices;

namespace HintedTextureSamplingProbe;

// Startup diagnostics only: resolve the image owning the actual function address
// obtained from Silk. A guessed package path is not loaded-library provenance.
internal static unsafe partial class ProviderLibrary
{
    internal static string Find(nint address)
    {
        if (address == 0) throw new InvalidOperationException("Missing live provider symbol.");
        if (OperatingSystem.IsMacOS())
        {
            if (DlAddress(address, out var info) == 0 || info.FileName == 0)
                throw new InvalidOperationException("dladdr could not identify the loaded WebGPU image.");
            return Marshal.PtrToStringUTF8(info.FileName) ?? throw new InvalidOperationException("Missing image name.");
        }
        // FROM_ADDRESS | UNCHANGED_REFCOUNT: borrowed inspection only, no load or
        // release of a driver/runtime image and no replacement of package assets.
        if (!GetModuleHandle(6, address, out nint module))
            throw new InvalidOperationException("Cannot identify the live WebGPU module: " + Marshal.GetLastPInvokeError());
        char* path = stackalloc char[32768];
        uint length = GetModuleFileName(module, path, 32768);
        if (length == 0 || length >= 32768) throw new InvalidOperationException("Invalid loaded WebGPU image path.");
        return new string(path, 0, checked((int)length));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo { internal nint FileName, BaseAddress, SymbolName, SymbolAddress; }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr")]
    private static partial int DlAddress(nint address, out DlInfo info);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandle(uint flags, nint address, out nint module);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static partial uint GetModuleFileName(nint module, char* fileName, uint size);
}
