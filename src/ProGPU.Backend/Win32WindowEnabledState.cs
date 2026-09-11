namespace ProGPU.Backend;

internal interface IWin32WindowEnabledOperations
{
    bool IsLocalWindow(nint window);
    void SetEnabled(nint window, bool enabled);
    bool IsEnabled(nint window);
}

internal static class Win32WindowEnabledState
{
    internal static bool Apply<T>(nint window, bool enabled, ref T operations)
        where T : IWin32WindowEnabledOperations
    {
        if (window == 0 || !operations.IsLocalWindow(window))
            return false;

        // EnableWindow returns the previous disabled state, not success. Its
        // synchronous WM_CANCELMODE/WM_ENABLE callbacks can also change state or
        // destroy the window. Check live ownership and the resulting state.
        operations.SetEnabled(window, enabled);
        return operations.IsLocalWindow(window) && operations.IsEnabled(window) == enabled;
    }
}
