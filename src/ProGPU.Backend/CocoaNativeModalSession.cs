using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

// AppKit ABI operations only. NativeWindowModalSession owns nesting and deferred
// end; the host still owns its GLFW window, dispatch queue, rendering and delays.
internal sealed unsafe partial class CocoaNativeModalSession : INativeModalSessionOperations
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private readonly nint _application;
    private nint _window, _view, _delegate;
    private readonly nint _begin, _poll, _end, _contentView, _windowDelegate;

    private CocoaNativeModalSession(nint application, nint window, nint view, nint windowDelegate)
    {
        _application = application;
        _window = window;
        _view = view;
        _delegate = windowDelegate;
        _begin = Selector("beginModalSessionForWindow:\0"u8);
        _poll = Selector("runModalSession:\0"u8);
        _end = Selector("endModalSession:\0"u8);
        _contentView = Selector("contentView\0"u8);
        _windowDelegate = Selector("delegate\0"u8);
    }

    public nint Window => _window;

    internal static CocoaNativeModalSession? TryCreate(nint window, nint previousWindow)
    {
        if (window == 0 || pthread_main_np() == 0 ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)) return null;
        using var pool = new Pool();
        nint application = Send(Class("NSApplication\0"u8), "sharedApplication\0"u8);
        if (application == 0 || Send(application, "modalWindow\0"u8) != previousWindow) return null;
        nint windows = Send(application, "windows\0"u8);
        // Check borrowed pointer identity before messaging it. Require visibility:
        // AppKit's implicit first-show centering must not replace host placement.
        if (windows == 0 || MessageArgument(windows, Selector("indexOfObjectIdenticalTo:\0"u8), window) == nint.MaxValue ||
            MessageBool(window, Selector("isVisible\0"u8)) == 0) return null;
        nint view = Send(window, "contentView\0"u8), windowDelegate = Send(window, "delegate\0"u8);
        if (view == 0) return null;
        var result = new CocoaNativeModalSession(application, window, view, windowDelegate);
        _ = Send(window, "retain\0"u8);
        _ = Send(view, "retain\0"u8);
        if (windowDelegate != 0) _ = Send(windowDelegate, "retain\0"u8);
        return result;
    }

    public nint Begin()
    {
        using var pool = new Pool();
        return MessageArgument(_application, _begin, _window);
    }

    public nint Poll(nint session)
    {
        // A retained NSWindow alone does not keep the GLFW host alive. Reject a
        // host that replaced/detached its view or delegate rather than dispatch
        // through obsolete native callbacks. Host teardown must wait for End.
        if (Message(_window, _contentView) != _view || Message(_window, _windowDelegate) != _delegate)
            throw new InvalidOperationException("The native modal window lost its host identity.");
        using var pool = new Pool();
        return MessageArgument(_application, _poll, session);
    }

    public void End(nint session)
    {
        using var pool = new Pool();
        MessageVoidArgument(_application, _end, session);
    }

    public void Dispose()
    {
        Release(_delegate); _delegate = 0;
        Release(_view); _view = 0;
        Release(_window); _window = 0;
    }

    private readonly ref struct Pool
    {
        private readonly nint _value;
        public Pool()
        {
            _value = Send(Send(Class("NSAutoreleasePool\0"u8), "alloc\0"u8), "init\0"u8);
            if (_value == 0) throw new InvalidOperationException("AppKit autorelease scope creation failed.");
        }
        public void Dispose() => Release(_value);
    }

    private static nint Class(ReadOnlySpan<byte> value) { fixed (byte* p = value) return objc_getClass(p); }
    private static nint Selector(ReadOnlySpan<byte> value) { fixed (byte* p = value) return sel_registerName(p); }
    private static nint Send(nint receiver, ReadOnlySpan<byte> selector) => Message(receiver, Selector(selector));
    private static void Release(nint value) { if (value != 0) MessageVoid(value, Selector("release\0"u8)); }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int pthread_main_np();
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint objc_getClass(byte* name);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint sel_registerName(byte* name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint Message(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte MessageBool(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidArgument(nint receiver, nint selector, nint argument);
}
