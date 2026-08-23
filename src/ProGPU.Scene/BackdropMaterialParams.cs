using System.Numerics;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public sealed class BackdropMaterialParams
{
    internal GpuTexture? CapturedHostBackdropTexture { get; set; }

    public Rect Rect { get; set; }
    public Vector4 CornerRadiiX { get; set; }
    public Vector4 CornerRadiiY { get; set; }
    public BackdropMaterialKind Kind { get; set; } = BackdropMaterialKind.Acrylic;
    public BackdropMaterialSource Source { get; set; } = BackdropMaterialSource.HostBackdrop;
    public Vector4 TintColor { get; set; } = Vector4.One;
    public Vector4 LuminosityColor { get; set; } = new(0.96f, 0.96f, 0.96f, 0.72f);
    public Vector4 FallbackColor { get; set; } = new(0.96f, 0.96f, 0.96f, 1f);
    public Vector4 NoiseColor { get; set; } = Vector4.One;
    public float TintOpacity { get; set; } = 1f;
    public float LuminosityOpacity { get; set; } = 1f;
    public float MaterialOpacity { get; set; } = 1f;
    public float NoiseOpacity { get; set; } = 0.0225f;
    public float BlurRadius { get; set; } = 30f;
    public float Saturation { get; set; } = 1.25f;
    public bool UseFallback { get; set; }
    public GpuTexture? SourceTexture { get; set; }
    public Rect SourceRect { get; set; }
    public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;
    public string? LastError { get; set; }

    public static BackdropMaterialParams FromBrush(
        BackdropMaterialBrush brush,
        Rect rect,
        Vector4 cornerRadiiX,
        Vector4 cornerRadiiY,
        GpuTexture? sourceTexture = null,
        Rect sourceRect = default)
    {
        ArgumentNullException.ThrowIfNull(brush);

        return new BackdropMaterialParams
        {
            Rect = rect,
            CornerRadiiX = cornerRadiiX,
            CornerRadiiY = cornerRadiiY,
            Kind = brush.Kind,
            Source = brush.Source,
            TintColor = brush.TintColor,
            LuminosityColor = brush.LuminosityColor,
            FallbackColor = brush.FallbackColor,
            NoiseColor = brush.NoiseColor,
            TintOpacity = brush.TintOpacity,
            LuminosityOpacity = brush.LuminosityOpacity,
            MaterialOpacity = brush.MaterialOpacity * brush.Opacity,
            NoiseOpacity = brush.NoiseOpacity,
            BlurRadius = brush.BlurRadius,
            Saturation = brush.Saturation,
            UseFallback = brush.UseFallback,
            SourceTexture = sourceTexture,
            SourceRect = sourceRect
        };
    }

    internal BackdropMaterialParams Translate(Vector2 translation)
    {
        return new BackdropMaterialParams
        {
            Rect = new Rect(Rect.Position + translation, Rect.Size),
            CornerRadiiX = CornerRadiiX,
            CornerRadiiY = CornerRadiiY,
            Kind = Kind,
            Source = Source,
            TintColor = TintColor,
            LuminosityColor = LuminosityColor,
            FallbackColor = FallbackColor,
            NoiseColor = NoiseColor,
            TintOpacity = TintOpacity,
            LuminosityOpacity = LuminosityOpacity,
            MaterialOpacity = MaterialOpacity,
            NoiseOpacity = NoiseOpacity,
            BlurRadius = BlurRadius,
            Saturation = Saturation,
            UseFallback = UseFallback,
            SourceTexture = SourceTexture,
            SourceRect = SourceRect,
            SamplingMode = SamplingMode,
            LastError = LastError
        };
    }
}
