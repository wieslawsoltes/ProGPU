using System.Numerics;
using ProGPU.Backend;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Media;

public abstract class SystemBackdrop
{
    private Vector4 _fallbackColor = new(0.96f, 0.96f, 0.96f, 1f);
    private BackdropMaterialBrush _fallbackBrush = new();

    internal abstract NativeWindowBackdrop NativeKind { get; }
    internal event Action? Changed;

    public Vector4 FallbackColor
    {
        get => _fallbackColor;
        set
        {
            if (_fallbackColor == value)
            {
                return;
            }
            _fallbackColor = value;
            Changed?.Invoke();
        }
    }

    public BackdropMaterialBrush FallbackBrush
    {
        get => _fallbackBrush;
        protected set
        {
            if (ReferenceEquals(_fallbackBrush, value))
            {
                return;
            }
            _fallbackBrush = value;
            Changed?.Invoke();
        }
    }
}

public enum MicaKind
{
    Base = 0,
    BaseAlt = 1
}

public sealed class MicaBackdrop : SystemBackdrop
{
    private MicaKind _kind;
    private bool _darkTheme;

    public MicaBackdrop()
    {
        UpdateFallback();
    }

    public MicaKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
            {
                return;
            }
            _kind = value;
            UpdateFallback();
        }
    }

    public bool DarkTheme
    {
        get => _darkTheme;
        set
        {
            if (_darkTheme == value)
            {
                return;
            }
            _darkTheme = value;
            UpdateFallback();
        }
    }

    internal override NativeWindowBackdrop NativeKind =>
        Kind == MicaKind.BaseAlt ? NativeWindowBackdrop.MicaAlt : NativeWindowBackdrop.Mica;

    private void UpdateFallback()
    {
        FallbackBrush = BackdropMaterialBrush.CreateMica(_darkTheme);
        if (_kind == MicaKind.BaseAlt)
        {
            FallbackBrush.TintOpacity *= 0.85f;
            FallbackBrush.LuminosityOpacity *= 0.9f;
        }
        FallbackColor = FallbackBrush.FallbackColor;
    }
}

public sealed class DesktopAcrylicBackdrop : SystemBackdrop
{
    public DesktopAcrylicBackdrop()
    {
        FallbackBrush = new BackdropMaterialBrush
        {
            Kind = BackdropMaterialKind.Acrylic,
            Source = BackdropMaterialSource.HostBackdrop,
            TintColor = new Vector4(0.96f, 0.96f, 0.96f, 0.58f),
            LuminosityColor = new Vector4(0.94f, 0.94f, 0.94f, 0.74f),
            FallbackColor = FallbackColor,
            BlurRadius = 30f,
            Saturation = 1.25f,
            NoiseOpacity = 0.0225f
        };
    }

    internal override NativeWindowBackdrop NativeKind => NativeWindowBackdrop.Acrylic;
}
