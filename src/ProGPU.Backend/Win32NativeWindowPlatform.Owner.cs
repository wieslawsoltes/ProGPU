namespace ProGPU.Backend;

internal sealed partial class Win32NativeWindowPlatform
{
    private readonly struct WindowOwnerOperations : IWin32WindowOwnerOperations
    {
        public bool IsLocalTopLevel(nint window)
        {
            var enabled = new WindowEnabledOperations();
            var access = new PopupOperations();
            return enabled.IsLocalWindow(window) && access.TryRead(window, GwlStyle, out nint style) &&
                (style.ToInt64() & 0x40000000L) == 0; // WS_CHILD
        }

        public bool TryGetOwner(nint window, out nint owner) =>
            new PopupOperations().TryRead(window, GwlpHwndParent, out owner);

        public bool TrySetOwner(nint window, nint owner) =>
            new PopupOperations().TryWrite(window, GwlpHwndParent, owner);
    }
}
