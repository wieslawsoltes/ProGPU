namespace ProGPU.Backend;

internal static class CocoaPopupFailure
{
    internal static void AttachCleanup(Exception primary, string key, Exception cleanup)
    {
        // Exception.Data can be overridden or read-only. Diagnostics must never
        // replace the original native/handler failure during retirement.
        try { primary.Data[key] = cleanup; }
        catch { }
    }
}
