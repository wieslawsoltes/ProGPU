using System.Runtime.InteropServices;
using WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

/// <summary>
/// Identifies a native window-system surface that Dawn can present to.
/// </summary>
public enum DawnNativeWindowKind
{
    Win32 = 0,
    Xlib = 1
}

/// <summary>
/// Owns the platform state required to create one or more Dawn surfaces for a
/// native Avalonia window.
/// </summary>
/// <remarks>
/// Construction and surface creation are O(1). The Xlib lane owns one display
/// connection for its lifetime; the Win32 lane borrows the HWND and process
/// module handle. No pixel storage or presentation texture is allocated here.
/// </remarks>
public sealed unsafe partial class DawnNativeWindowSource : IDisposable
{
    private readonly bool _ownsDisplay;
    private nint _displayOrInstance;
    private nint _window;
    private bool _disposed;

    private DawnNativeWindowSource(
        DawnNativeWindowKind kind,
        nint displayOrInstance,
        nint window,
        bool ownsDisplay)
    {
        Kind = kind;
        _displayOrInstance = displayOrInstance;
        _window = window;
        _ownsDisplay = ownsDisplay;
    }

    public DawnNativeWindowKind Kind { get; }

    public BackendType BackendType =>
        Kind == DawnNativeWindowKind.Win32
            ? BackendType.D3D12
            : BackendType.Vulkan;

    public string BackendName =>
        Kind == DawnNativeWindowKind.Win32
            ? "Dawn D3D12"
            : "Dawn Vulkan/Xlib";

    public static DawnNativeWindowSource CreateWin32(nint hwnd)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "A Dawn HWND surface requires Windows.");
        }
        if (hwnd == 0)
        {
            throw new ArgumentException(
                "A valid HWND is required.",
                nameof(hwnd));
        }

        nint hinstance = GetModuleHandleW(0);
        if (hinstance == 0)
        {
            throw new InvalidOperationException(
                "GetModuleHandleW could not resolve the process module.");
        }
        return new DawnNativeWindowSource(
            DawnNativeWindowKind.Win32,
            hinstance,
            hwnd,
            ownsDisplay: false);
    }

    public static DawnNativeWindowSource CreateXlib(nint xid)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "A Dawn Xlib surface requires Linux.");
        }
        if (xid == 0)
        {
            throw new ArgumentException(
                "A valid XID is required.",
                nameof(xid));
        }

        nint display = XOpenDisplay(0);
        if (display == 0)
        {
            throw new NotSupportedException(
                "XOpenDisplay could not connect to the active X11 display.");
        }
        return new DawnNativeWindowSource(
            DawnNativeWindowKind.Xlib,
            display,
            xid,
            ownsDisplay: true);
    }

    /// <summary>
    /// Maps Avalonia's stable native handle descriptors to a Dawn WSI kind.
    /// </summary>
    public static bool TryGetKind(
        string? handleDescriptor,
        bool isWindows,
        bool isLinux,
        out DawnNativeWindowKind kind)
    {
        if (isWindows &&
            string.Equals(
                handleDescriptor,
                "HWND",
                StringComparison.Ordinal))
        {
            kind = DawnNativeWindowKind.Win32;
            return true;
        }
        if (isLinux &&
            string.Equals(
                handleDescriptor,
                "XID",
                StringComparison.Ordinal))
        {
            kind = DawnNativeWindowKind.Xlib;
            return true;
        }

        kind = default;
        return false;
    }

    internal SurfaceHandle CreateSurface(InstanceHandle instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (instance == InstanceHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn instance is required.",
                nameof(instance));
        }

        return Kind switch
        {
            DawnNativeWindowKind.Win32 =>
                CreateWin32Surface(instance),
            DawnNativeWindowKind.Xlib =>
                CreateXlibSurface(instance),
            _ => throw new NotSupportedException(
                $"Unsupported Dawn window kind {Kind}.")
        };
    }

    private SurfaceHandle CreateWin32Surface(InstanceHandle instance)
    {
        var source = new SurfaceSourceWindowsHWNDFFI
        {
            Chain = new ChainedStruct
            {
                SType = SType.SurfaceSourceWindowsHWND
            },
            Hinstance = (void*)_displayOrInstance,
            Hwnd = (void*)_window
        };
        var descriptor = new SurfaceDescriptorFFI
        {
            NextInChain = &source.Chain
        };
        return instance.CreateSurface(descriptor);
    }

    private SurfaceHandle CreateXlibSurface(InstanceHandle instance)
    {
        var source = new SurfaceSourceXlibWindowFFI
        {
            Chain = new ChainedStruct
            {
                SType = SType.SurfaceSourceXlibWindow
            },
            Display = (void*)_displayOrInstance,
            Window = unchecked((ulong)_window)
        };
        var descriptor = new SurfaceDescriptorFFI
        {
            NextInChain = &source.Chain
        };
        return instance.CreateSurface(descriptor);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsDisplay && _displayOrInstance != 0)
        {
            XCloseDisplay(_displayOrInstance);
        }
        _displayOrInstance = 0;
        _window = 0;
        _disposed = true;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport(
        "libX11.so.6",
        EntryPoint = "XOpenDisplay")]
    private static partial nint XOpenDisplay(nint displayName);

    [LibraryImport(
        "libX11.so.6",
        EntryPoint = "XCloseDisplay")]
    private static partial int XCloseDisplay(nint display);
}
