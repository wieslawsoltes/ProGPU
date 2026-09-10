namespace ProGPU.Backend;

internal interface IX11PopupOperations
{
    bool AdmitHiddenPair();
    bool SetOwner();
    bool SetOverrideRedirect();
    bool SetMenuTypes();
    bool ConfirmHiddenConfiguration();
}

internal static class X11PopupConfiguration
{
    // Every operation is required. Request submission alone is not confirmation
    // that all server-side state exists. The caller destroys a rejected popup.
    internal static bool Apply<T>(ref T operations) where T : IX11PopupOperations =>
        operations.AdmitHiddenPair() && operations.SetOwner() &&
        operations.SetOverrideRedirect() && operations.SetMenuTypes() &&
        operations.ConfirmHiddenConfiguration();
}
