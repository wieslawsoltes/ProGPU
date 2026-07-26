using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Platform;

namespace Avalonia.SilkNet;

/// <summary>
/// Hosts Avalonia popups in an independently composed Silk.NET window.
/// </summary>
internal sealed class SilkNetPopupImpl : WindowImpl, IPopupImpl
{
    private readonly WindowImpl _parent;

    public SilkNetPopupImpl(WindowImpl parent)
    {
        _parent = parent;
        SetParent(parent);
        ShowTaskbarIcon(false);
        SetTopmost(true);
        CanResize(false);
        SetCanMinimize(false);
        SetCanMaximize(false);
#if AVALONIA11
        SetSystemDecorations(SystemDecorations.None);
#else
        SetWindowDecorations(WindowDecorations.None);
#endif

        PopupPositioner = new ManagedPopupPositioner(
            new ManagedPopupPositionerPopupImplHelper(parent, MoveAndResize));
    }

    public IPopupPositioner PopupPositioner { get; }

    public override void Show(bool activate, bool isDialog)
    {
        // Popup roots must not steal activation from their owning window.
        base.Show(activate: false, isDialog);
    }

    public override IPopupImpl CreatePopup() => new SilkNetPopupImpl(this);

    public void SetWindowManagerAddShadowHint(bool enabled)
    {
        // Silk/GLFW doesn't expose a portable shadow toggle. Native platforms
        // retain their default popup shadow policy.
    }

    public void TakeFocus()
    {
        WindowImpl root = _parent;
        while (root.NativeParent is { } parent)
        {
            root = parent;
        }
        root.Activate();
    }

    private void MoveAndResize(PixelPoint position, Size size, double scaling)
    {
        Move(position);
        Resize(size, WindowResizeReason.Layout);
    }
}
