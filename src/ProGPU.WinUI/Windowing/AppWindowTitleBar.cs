using ProGPU.Backend;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.UI;

namespace Microsoft.UI.Windowing;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class AppWindowTitleBar
{
    private readonly AppWindow _owner;
    private RectInt32[] _dragRectangles = [];
    private IconShowOptions _iconShowOptions;
    private TitleBarHeightOption _preferredHeightOption;
    private TitleBarTheme _preferredTheme;
    private bool _extendsContentIntoTitleBar;

    internal AppWindowTitleBar(AppWindow owner)
    {
        _owner = owner;
    }

    public Color? BackgroundColor { get; set; }

    public Color? ButtonBackgroundColor { get; set; }

    public Color? ButtonForegroundColor { get; set; }

    public Color? ButtonHoverBackgroundColor { get; set; }

    public Color? ButtonHoverForegroundColor { get; set; }

    public Color? ButtonInactiveBackgroundColor { get; set; }

    public Color? ButtonInactiveForegroundColor { get; set; }

    public Color? ButtonPressedBackgroundColor { get; set; }

    public Color? ButtonPressedForegroundColor { get; set; }

    public Color? ForegroundColor { get; set; }

    public Color? InactiveBackgroundColor { get; set; }

    public Color? InactiveForegroundColor { get; set; }

    public bool ExtendsContentIntoTitleBar
    {
        get => _extendsContentIntoTitleBar;
        set
        {
            if (_extendsContentIntoTitleBar == value)
                return;
            _extendsContentIntoTitleBar = value;
            Apply();
        }
    }

    public int Height => _preferredHeightOption switch
    {
        TitleBarHeightOption.Collapsed => 0,
        TitleBarHeightOption.Tall => 48,
        _ => Math.Max(0, _owner.FrameInsets.Top)
    };

    public IconShowOptions IconShowOptions
    {
        get => _iconShowOptions;
        set
        {
            if (_iconShowOptions == value)
                return;
            _iconShowOptions = value;
            _owner.NotifyTitleBarChanged();
        }
    }

    public int LeftInset => Math.Max(0, _owner.FrameInsets.Left);

    public TitleBarHeightOption PreferredHeightOption
    {
        get => _preferredHeightOption;
        set
        {
            if (_preferredHeightOption == value)
                return;
            _preferredHeightOption = value;
            Apply();
        }
    }

    public TitleBarTheme PreferredTheme
    {
        get => _preferredTheme;
        set
        {
            if (_preferredTheme == value)
                return;
            _preferredTheme = value;
            Apply();
        }
    }

    public int RightInset => Math.Max(0, _owner.FrameInsets.Right);

    public static bool IsCustomizationSupported() =>
        NativeWindowCapabilities
            .ForKind(NativeWindowCapabilities.DetectCurrentKind())
            .Supports(NativeWindowFeatures.ClientAreaExtension);

    public void ResetToDefault()
    {
        BackgroundColor = null;
        ButtonBackgroundColor = null;
        ButtonForegroundColor = null;
        ButtonHoverBackgroundColor = null;
        ButtonHoverForegroundColor = null;
        ButtonInactiveBackgroundColor = null;
        ButtonInactiveForegroundColor = null;
        ButtonPressedBackgroundColor = null;
        ButtonPressedForegroundColor = null;
        ForegroundColor = null;
        InactiveBackgroundColor = null;
        InactiveForegroundColor = null;
        _dragRectangles = [];
        _iconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
        _preferredHeightOption = TitleBarHeightOption.Standard;
        _preferredTheme = TitleBarTheme.Legacy;
        _extendsContentIntoTitleBar = false;
        Apply();
    }

    public void SetDragRectangles(RectInt32[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _dragRectangles = (RectInt32[])value.Clone();
        _owner.NotifyTitleBarChanged();
    }

    internal ReadOnlySpan<RectInt32> DragRectangles => _dragRectangles;

    private void Apply()
    {
        _owner.VerifyAccess();
        _owner.XamlWindow.ExtendsContentIntoTitleBar =
            _extendsContentIntoTitleBar;
        _owner.XamlWindow.TitleBarHeight =
            _preferredHeightOption switch
            {
                TitleBarHeightOption.Collapsed => 0d,
                TitleBarHeightOption.Tall => 48d,
                _ => -1d
            };
        _owner.XamlWindow.NativeWindowController?.SetTheme(
            _preferredTheme switch
            {
                TitleBarTheme.Dark => NativeWindowTheme.Dark,
                TitleBarTheme.Light => NativeWindowTheme.Light,
                _ => NativeWindowTheme.Default
            });
        _owner.NotifyTitleBarChanged();
    }
}
