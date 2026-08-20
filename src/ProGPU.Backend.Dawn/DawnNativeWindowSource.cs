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
    Xlib = 1,
    Android = 2,
    MetalLayer = 3,
    Wayland = 4
}

/// <summary>
/// Owns the platform state required to create one or more Dawn surfaces for a
/// native Avalonia window.
/// </summary>
/// <remarks>
/// Construction and surface creation are O(1). The Xlib lane owns one display
/// connection for its lifetime; the Win32 lane borrows the HWND and process
/// module handle. Disposing the source prevents new surfaces and defers owned
/// native-resource release until existing surfaces release their lifetime
/// leases. No pixel storage or presentation texture is allocated here.
/// </remarks>
public sealed unsafe partial class DawnNativeWindowSource : IDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly bool _ownsDisplay;
    private readonly bool _ownsWindow;
    private nint _displayOrInstance;
    private nint _window;
    private int _surfaceLeaseCount;
    private bool _disposed;

    private DawnNativeWindowSource(
        DawnNativeWindowKind kind,
        nint displayOrInstance,
        nint window,
        bool ownsDisplay,
        bool ownsWindow = false)
    {
        Kind = kind;
        _displayOrInstance = displayOrInstance;
        _window = window;
        _ownsDisplay = ownsDisplay;
        _ownsWindow = ownsWindow;
    }

    public DawnNativeWindowKind Kind { get; }

    public BackendType BackendType =>
        Kind switch
        {
            DawnNativeWindowKind.Win32 => BackendType.D3D12,
            DawnNativeWindowKind.MetalLayer => BackendType.Metal,
            _ => BackendType.Vulkan
        };

    public string BackendName =>
        Kind switch
        {
            DawnNativeWindowKind.Win32 => "Dawn D3D12",
            DawnNativeWindowKind.Xlib => "Dawn Vulkan/Xlib",
            DawnNativeWindowKind.Wayland => "Dawn Vulkan/Wayland",
            DawnNativeWindowKind.Android => "Dawn Vulkan/Android",
            DawnNativeWindowKind.MetalLayer => "Dawn Metal/CAMetalLayer",
            _ => "Dawn"
        };

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

    public static DawnNativeWindowSource CreateWayland(
        nint display,
        nint surface)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "A Dawn Wayland surface requires Linux.");
        }
        if (display == 0 || surface == 0)
        {
            throw new ArgumentException(
                "Valid Wayland display and surface handles are required.");
        }
        return new DawnNativeWindowSource(
            DawnNativeWindowKind.Wayland,
            display,
            surface,
            ownsDisplay: false);
    }

    public static DawnNativeWindowSource CreateAndroid(
        nint nativeWindow)
    {
        if (!OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "A Dawn ANativeWindow surface requires Android.");
        }
        if (nativeWindow == 0)
        {
            throw new ArgumentException(
                "A valid ANativeWindow is required.",
                nameof(nativeWindow));
        }

        return new DawnNativeWindowSource(
            DawnNativeWindowKind.Android,
            0,
            nativeWindow,
            ownsDisplay: false);
    }

    public static DawnNativeWindowSource CreateMetalLayer(
        nint metalLayer)
    {
        if (!OperatingSystem.IsMacOS() &&
            !OperatingSystem.IsIOS())
        {
            throw new PlatformNotSupportedException(
                "A Dawn CAMetalLayer surface requires an Apple platform.");
        }
        if (metalLayer == 0)
        {
            throw new ArgumentException(
                "A valid CAMetalLayer is required.",
                nameof(metalLayer));
        }

        return new DawnNativeWindowSource(
            DawnNativeWindowKind.MetalLayer,
            0,
            metalLayer,
            ownsDisplay: false);
    }

    /// <summary>
    /// Installs and retains a CAMetalLayer as the backing layer of a Cocoa
    /// NSWindow content view. AppKit retains the layer after this source
    /// releases its construction reference.
    /// </summary>
    public static DawnNativeWindowSource CreateCocoaWindow(
        nint nsWindow)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "A Cocoa CAMetalLayer surface requires macOS.");
        }
        if (nsWindow == 0)
        {
            throw new ArgumentException(
                "A valid NSWindow is required.",
                nameof(nsWindow));
        }

        nint contentView = SendObject(
            nsWindow,
            Selector("contentView"));
        nint metalLayerClass = objc_getClass("CAMetalLayer");
        if (contentView == 0 || metalLayerClass == 0)
        {
            throw new NotSupportedException(
                "AppKit did not expose an NSView and CAMetalLayer.");
        }

        nint layer = SendObject(
            metalLayerClass,
            Selector("layer"));
        if (layer == 0)
        {
            throw new InvalidOperationException(
                "CAMetalLayer allocation failed.");
        }

        SendObject(layer, Selector("retain"));
        try
        {
            SendVoidBoolean(
                contentView,
                Selector("setWantsLayer:"),
                value: true);
            SendVoidObject(
                contentView,
                Selector("setLayer:"),
                layer);
            double scale = SendDouble(
                nsWindow,
                Selector("backingScaleFactor"));
            if (double.IsFinite(scale) && scale > 0d)
            {
                SendVoidDouble(
                    layer,
                    Selector("setContentsScale:"),
                    scale);
            }
        }
        catch
        {
            SendObject(layer, Selector("release"));
            throw;
        }

        return new DawnNativeWindowSource(
            DawnNativeWindowKind.MetalLayer,
            0,
            layer,
            ownsDisplay: false,
            ownsWindow: true);
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

    internal SurfaceHandle CreateSurface(
        InstanceHandle instance,
        out IDisposable lifetimeLease)
    {
        if (instance == InstanceHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn instance is required.",
                nameof(instance));
        }

        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _surfaceLeaseCount++;
            try
            {
                SurfaceHandle surface = Kind switch
                {
                    DawnNativeWindowKind.Win32 =>
                        CreateWin32Surface(instance),
                    DawnNativeWindowKind.Xlib =>
                        CreateXlibSurface(instance),
                    DawnNativeWindowKind.Wayland =>
                        CreateWaylandSurface(instance),
                    DawnNativeWindowKind.Android =>
                        CreateAndroidSurface(instance),
                    DawnNativeWindowKind.MetalLayer =>
                        CreateMetalLayerSurface(instance),
                    _ => throw new NotSupportedException(
                        $"Unsupported Dawn window kind {Kind}.")
                };
                lifetimeLease = new SurfaceLifetimeLease(this);
                return surface;
            }
            catch
            {
                _surfaceLeaseCount--;
                throw;
            }
        }
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

    private SurfaceHandle CreateWaylandSurface(
        InstanceHandle instance)
    {
        var source =
            new SurfaceSourceWaylandSurfaceFFI
            {
                Chain = new ChainedStruct
                {
                    SType =
                        SType.SurfaceSourceWaylandSurface
                },
                Display = (void*)_displayOrInstance,
                Surface = (void*)_window
            };
        var descriptor = new SurfaceDescriptorFFI
        {
            NextInChain = &source.Chain
        };
        return instance.CreateSurface(descriptor);
    }

    private SurfaceHandle CreateAndroidSurface(
        InstanceHandle instance)
    {
        var source = new SurfaceSourceAndroidNativeWindowFFI
        {
            Chain = new ChainedStruct
            {
                SType = SType.SurfaceSourceAndroidNativeWindow
            },
            Window = (void*)_window
        };
        var descriptor = new SurfaceDescriptorFFI
        {
            NextInChain = &source.Chain
        };
        return instance.CreateSurface(descriptor);
    }

    private SurfaceHandle CreateMetalLayerSurface(
        InstanceHandle instance)
    {
        var source = new SurfaceSourceMetalLayerFFI
        {
            Chain = new ChainedStruct
            {
                SType = SType.SurfaceSourceMetalLayer
            },
            Layer = (void*)_window
        };
        var descriptor = new SurfaceDescriptorFFI
        {
            NextInChain = &source.Chain
        };
        return instance.CreateSurface(descriptor);
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_surfaceLeaseCount == 0)
            {
                ReleaseNativeResources();
            }
        }
    }

    private void ReleaseSurfaceLease()
    {
        lock (_lifetimeGate)
        {
            if (_surfaceLeaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "The Dawn native surface lifetime lease is unbalanced.");
            }

            _surfaceLeaseCount--;
            if (_disposed && _surfaceLeaseCount == 0)
            {
                ReleaseNativeResources();
            }
        }
    }

    private void ReleaseNativeResources()
    {
        if (_ownsDisplay && _displayOrInstance != 0)
        {
            XCloseDisplay(_displayOrInstance);
        }
        if (_ownsWindow && _window != 0)
        {
            SendObject(_window, Selector("release"));
        }
        _displayOrInstance = 0;
        _window = 0;
    }

    private sealed class SurfaceLifetimeLease : IDisposable
    {
        private DawnNativeWindowSource? _owner;

        internal SurfaceLifetimeLease(DawnNativeWindowSource owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?
                .ReleaseSurfaceLease();
        }
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

    private const string ObjectiveCLibrary =
        "/usr/lib/libobjc.A.dylib";

    private static nint Selector(string name)
    {
        nint selector = sel_registerName(name);
        if (selector == 0)
        {
            throw new InvalidOperationException(
                $"Objective-C selector '{name}' is unavailable.");
        }
        return selector;
    }

    [LibraryImport(
        ObjectiveCLibrary,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(
        ObjectiveCLibrary,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    private static partial nint SendObject(
        nint receiver,
        nint selector);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    private static partial void SendVoidObject(
        nint receiver,
        nint selector,
        nint value);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    private static partial void SendVoidBoolean(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    private static partial double SendDouble(
        nint receiver,
        nint selector);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    private static partial void SendVoidDouble(
        nint receiver,
        nint selector,
        double value);
}
