using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

namespace ProGPU.Backend.Native;

/// <summary>
/// Creates and describes the separately packaged provider-resolved Dawn renderer.
/// </summary>
/// <remarks>
/// The caller retains device/canvas ownership in <see cref="DawnGpuContext"/>.
/// This adapter retains only the process module that supplies WebGPU procedures
/// and never imports or owns an OS surface handle.
/// </remarks>
public static unsafe class NativeDawnAdapter
{
    private const string IosDawnFramework =
        "@rpath/webgpu_dawn.framework/webgpu_dawn";
    private static readonly object NativeLibrarySync = new();
    private static nint s_dawnLibrary;

    public const uint AdapterAbiVersion = 1;
    public const uint RequiredProviderAbiVersion = 2;
    public const uint BackendAbi = 2;

    static NativeDawnAdapter()
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            NativeLibrary.SetDllImportResolver(
                typeof(NativeDawnAdapter).Assembly,
                ResolveNativeRendererImport);
        }
    }

    /// <summary>
    /// Creates the C++ renderer over the exact device and procedure exports
    /// owned by an existing typed Dawn context.
    /// </summary>
    public static NativeCompositor CreateCompositor(
        DawnGpuContext context,
        TextureFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        GetInfo();
        nint module = GetDawnLibrary();
        DawnNativeDeviceHandles handles = context.GetNativeDeviceHandles();
        return NativeCompositor.CreateDawn(
            context.Context,
            targetFormat,
            handles.Instance,
            handles.Device,
            handles.Queue,
            module,
            (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint>)&ResolveDawnProc);
    }

    /// <summary>
    /// Recreates a lost C++ renderer on a replacement typed Dawn device while
    /// preserving its immutable CPU scene snapshot transactionally.
    /// </summary>
    public static NativeCompositor RecreateCompositor(
        NativeCompositor source,
        DawnGpuContext replacementContext)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacementContext);
        nint module = GetDawnLibrary();
        DawnNativeDeviceHandles handles =
            replacementContext.GetNativeDeviceHandles();
        return source.RecreateDawn(
            replacementContext.Context,
            handles.Instance,
            handles.Device,
            handles.Queue,
            module,
            (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint>)&ResolveDawnProc);
    }

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

    /// <summary>
    /// Runs the provider-resolved C++ semantic stream validator without
    /// creating or mutating a GPU renderer.
    /// </summary>
    public static NativeSceneUpdateMetrics ValidateScene(
        ReadOnlySpan<byte> stream) =>
        NativeCompositor.ValidateScene(
            stream,
            NativeRendererInteropKind.Dawn);

    private static nint GetDawnLibrary()
    {
        lock (NativeLibrarySync)
        {
            if (s_dawnLibrary != 0)
            {
                return s_dawnLibrary;
            }
            string library;
            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            {
                library = IosDawnFramework;
            }
            else if (OperatingSystem.IsWindows())
            {
                library = "webgpu_dawn.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                library = "libwebgpu_dawn.dylib";
            }
            else
            {
                library = "libwebgpu_dawn.so";
            }
            if (!NativeLibrary.TryLoad(library, out s_dawnLibrary))
            {
                throw new DllNotFoundException(
                    $"The exact Dawn procedure provider '{library}' could not be loaded.");
            }
            return s_dawnLibrary;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ResolveDawnProc(nint context, byte* name)
    {
        if (context == 0 || name == null)
        {
            return 0;
        }
        try
        {
            string? symbol = Marshal.PtrToStringUTF8((nint)name);
            return symbol != null &&
                   NativeLibrary.TryGetExport(context, symbol, out nint address)
                ? address
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static nint ResolveNativeRendererImport(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath) =>
        string.Equals(
            libraryName,
            NativeDawnMethods.LibraryName,
            StringComparison.Ordinal)
            ? NativeLibrary.GetMainProgramHandle()
            : 0;
}
