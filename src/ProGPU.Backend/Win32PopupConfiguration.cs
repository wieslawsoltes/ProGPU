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

    internal static bool Apply<T>(nint owner, nint popup, ref T api) where T : IWin32PopupOperations =>
        Apply(owner, popup, ref api, out _);

    internal static bool Apply<T>(nint owner, nint popup, ref T api,
        out Win32PopupConfigurationFailure failure) where T : IWin32PopupOperations
    {
        failure = Win32PopupConfigurationFailure.None;
        if (owner == 0 || popup == 0 || owner == popup)
        {
            failure = Win32PopupConfigurationFailure.InvalidIdentity;
            return false;
        }
        if (!api.AreLocalWindows(owner, popup))
        {
            failure = Win32PopupConfigurationFailure.NonlocalWindows;
            return false;
        }
        if (!api.TryRead(owner, Style, out nint ownerStyle))
        {
            failure = Win32PopupConfigurationFailure.OwnerStyleRead;
            return false;
        }
        if (!api.TryRead(popup, Style, out nint style))
        {
            failure = Win32PopupConfigurationFailure.PopupStyleRead;
            return false;
        }
        if (!api.TryRead(popup, ExtendedStyle, out nint extended))
        {
            failure = Win32PopupConfigurationFailure.PopupExtendedStyleRead;
            return false;
        }
        if (!api.TryRead(popup, Owner, out nint previousOwner))
        {
            failure = Win32PopupConfigurationFailure.PreviousOwnerRead;
            return false;
        }
        if ((unchecked((uint)ownerStyle) & Child) != 0)
        {
            failure = Win32PopupConfigurationFailure.ChildOwner;
            return false;
        }
        if ((unchecked((uint)style) & Child) != 0)
        {
            failure = Win32PopupConfigurationFailure.ChildPopup;
            return false;
        }
        if ((unchecked((uint)style) & Visible) != 0)
        {
            failure = Win32PopupConfigurationFailure.VisiblePopup;
            return false;
        }

        uint popupStyle = (unchecked((uint)style) | Popup) & ~OverlappedChrome;
        uint popupExtended = (unchecked((uint)extended) | NoActivate | ToolWindow) & ~AppWindow;
        if (!api.TryWrite(popup, Owner, owner))
            failure = Win32PopupConfigurationFailure.OwnerWrite;
        else if (!api.TryWrite(popup, Style, unchecked((nint)(int)popupStyle)))
            failure = Win32PopupConfigurationFailure.PopupStyleWrite;
        else if (!api.TryWrite(popup, ExtendedStyle, unchecked((nint)(int)popupExtended)))
            failure = Win32PopupConfigurationFailure.PopupExtendedStyleWrite;
        else if (!api.InstallNonActivationHook(popup))
            failure = Win32PopupConfigurationFailure.NonActivationHook;
        else if (!api.RefreshFrame(popup))
            failure = Win32PopupConfigurationFailure.FrameRefresh;
        else return true;

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

internal enum Win32PopupConfigurationFailure
{
    None,
    InvalidIdentity,
    NonlocalWindows,
    OwnerStyleRead,
    PopupStyleRead,
    PopupExtendedStyleRead,
    PreviousOwnerRead,
    ChildOwner,
    ChildPopup,
    VisiblePopup,
    OwnerWrite,
    PopupStyleWrite,
    PopupExtendedStyleWrite,
    NonActivationHook,
    FrameRefresh
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
