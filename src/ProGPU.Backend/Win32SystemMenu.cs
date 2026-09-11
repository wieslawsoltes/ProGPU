namespace ProGPU.Backend;

// The injected native operations keep admission, reentrancy and command delivery
// testable without displaying UI. This is bounded control flow, not CPU compute.
internal interface IWin32SystemMenuOperations
{
    bool TryGetLocalMenu(nint owner, out nint menu);
    bool RightAligned { get; }
    bool TryTrack(nint owner, nint menu, NativeWindowPoint position, uint flags, out uint command);
    bool PostSystemCommand(nint owner, uint command);
}

internal static class Win32SystemMenu
{
    internal static bool Show<T>(nint owner, NativeWindowPoint position, ref T api)
        where T : IWin32SystemMenuOperations
    {
        if (owner == 0 || !api.TryGetLocalMenu(owner, out nint menu) || menu == 0)
            return false;
        // Return the command once; retain native initialization notifications so
        // the owner can apply standard enabled states and custom menu entries.
        uint flags = 0x0100u | 0x0002u | (api.RightAligned ? 0x0008u : 0u);
        if (!api.TryTrack(owner, menu, position, flags, out uint command))
            return false;
        if (command == 0) return true;
        // Menu tracking dispatches messages. The owner/menu may have been
        // destroyed or replaced while a selection was pending.
        return api.TryGetLocalMenu(owner, out nint currentMenu) && currentMenu == menu &&
            api.PostSystemCommand(owner, command);
    }
}
