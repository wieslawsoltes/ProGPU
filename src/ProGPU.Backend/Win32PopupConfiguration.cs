namespace ProGPU.Backend;

// Original implementation of the documented Win32 hidden owned-window contract.
// O(1) calls/state and one native lifetime hook; no managed allocation or device work.
internal static class Win32PopupConfiguration
{
    internal const int Style = -16;
    internal const int ExtendedStyle = -20;
    internal const int Owner = -8;
    private const uint Child = 0x40000000;
    private const uint Visible = 0x10000000;
    private const uint Popup = 0x80000000;
    private const uint OverlappedChrome = 0x00cf0000;
    private const uint NoActivate = 0x08000000;
    private const uint ToolWindow = 0x00000080;
    private const uint AppWindow = 0x00040000;

    internal static bool Apply<T>(nint owner, nint popup, ref T api) where T : IWin32PopupOperations
    {
        if (owner == 0 || popup == 0 || owner == popup || !api.AreLocalWindows(owner, popup) ||
            !api.TryRead(owner, Style, out nint ownerStyle) ||
            !api.TryRead(popup, Style, out nint style) ||
            !api.TryRead(popup, ExtendedStyle, out nint extended) ||
            !api.TryRead(popup, Owner, out nint previousOwner) ||
            (unchecked((uint)ownerStyle) & Child) != 0 || (unchecked((uint)style) & (Child | Visible)) != 0) return false;

        uint popupStyle = (unchecked((uint)style) | Popup) & ~OverlappedChrome;
        uint popupExtended = (unchecked((uint)extended) | NoActivate | ToolWindow) & ~AppWindow;
        if (api.TryWrite(popup, Owner, owner) &&
            api.TryWrite(popup, Style, unchecked((nint)(int)popupStyle)) &&
            api.TryWrite(popup, ExtendedStyle, unchecked((nint)(int)popupExtended)) &&
            api.InstallNonActivationHook(popup) &&
            api.RefreshFrame(popup)) return true;

        // Restore all original attributes on any failure. Restoration itself may
        // fail (for example if the HWND was destroyed); callers must discard it.
        api.RemoveNonActivationHook(popup);
        _ = api.TryWrite(popup, ExtendedStyle, extended);
        _ = api.TryWrite(popup, Style, style);
        _ = api.TryWrite(popup, Owner, previousOwner);
        _ = api.RefreshFrame(popup);
        return false;
    }
}

internal interface IWin32PopupOperations
{
    bool AreLocalWindows(nint owner, nint popup);
    bool TryRead(nint window, int index, out nint value);
    bool TryWrite(nint window, int index, nint value);
    bool RefreshFrame(nint window);
    bool InstallNonActivationHook(nint window);
    void RemoveNonActivationHook(nint window);
}
