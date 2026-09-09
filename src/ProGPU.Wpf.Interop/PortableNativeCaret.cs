namespace ProGPU.Wpf.Interop;

/// <summary>Optional OS caret mirror; source WPF continues to draw its real caret.</summary>
public interface IPortableNativeCaretService
{
    /// <summary>
    /// Borrow an opaque source caret identity and its rectangle in the native
    /// source's client coordinate units. False explicitly means no OS mirror was
    /// established; it must never cause fallback to source-local HWND APIs.
    /// Calls and release are confined to the source/window thread.
    /// </summary>
    bool TryUpdate(object owner, in PortableRect clientBounds);
    /// <summary>Release only this identity, never a newer caret owner.</summary>
    void Release(object owner);
}

/// <summary>Typed host attachment, independent of source assembly identities.</summary>
public interface IPortableNativeCaretHost
{
    IPortableNativeCaretService? NativeCaretService { get; set; }
}
