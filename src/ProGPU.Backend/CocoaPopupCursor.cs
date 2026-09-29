using Silk.NET.Core;
using Silk.NET.Input;

namespace ProGPU.Backend;

internal sealed class CocoaPopupCursor(CocoaPopupWindow window, Action checkConnected) : ICursor
{
    private StandardCursor _standard = StandardCursor.Arrow;
    private CursorMode _mode = CursorMode.Normal;

    public bool IsSupported(StandardCursor cursor)
    {
        checkConnected();
        return window.SupportsCursor(cursor);
    }
    public bool IsSupported(CursorMode mode) => mode is CursorMode.Normal or CursorMode.Hidden;
    public StandardCursor StandardCursor
    {
        get => _standard;
        set { Apply(value, _mode); _standard = value; }
    }
    public CursorMode CursorMode
    {
        get => _mode;
        set
        {
            if (!IsSupported(value)) throw new NotSupportedException("Owned popup input is absolute and cannot lock the system pointer.");
            Apply(_standard, value);
            _mode = value;
        }
    }
    public CursorType Type
    {
        get => CursorType.Standard;
        set { checkConnected(); if (value != CursorType.Standard) throw new NotSupportedException("Custom popup cursor images are not admitted."); }
    }
    public bool IsConfined
    {
        get => false;
        set { checkConnected(); if (value) throw new NotSupportedException("An owned popup does not confine the global pointer."); }
    }
    public RawImage Image
    {
        get => throw new NotSupportedException("Native standard cursor pixels are not a custom image.");
        set => throw new NotSupportedException("Custom popup cursor images are not admitted.");
    }
    public int HotspotX { get => throw new NotSupportedException(); set => throw new NotSupportedException("The native cursor owns its hotspot."); }
    public int HotspotY { get => throw new NotSupportedException(); set => throw new NotSupportedException("The native cursor owns its hotspot."); }
    private void Apply(StandardCursor cursor, CursorMode mode)
    {
        checkConnected();
        if (!window.SetCursor(cursor, mode == CursorMode.Hidden))
            throw new NotSupportedException("The owned native view rejected the requested cursor.");
    }
}
