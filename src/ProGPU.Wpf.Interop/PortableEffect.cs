namespace ProGPU.Wpf.Interop;

public interface IPortableEffectSource
{
    bool TryGetPortableEffect(out PortableEffect effect);
}

public interface IPortableBitmapEffectInputSource
{
    bool TryGetPortableBitmapEffectInput(out PortableBitmapEffectInput input);
}

public enum PortableEffectKind
{
    Blur = 0,
    DropShadow = 1
}

public enum PortableBlurKernel
{
    Gaussian = 0,
    Box = 1
}

public enum PortableEffectRenderingBias
{
    Performance = 0,
    Quality = 1
}

public sealed class PortableBitmapEffectInput
{
    public PortableBitmapEffectInput(
        bool usesContextInput,
        bool hasDefaultAreaToApplyEffect)
    {
        UsesContextInput = usesContextInput;
        HasDefaultAreaToApplyEffect = hasDefaultAreaToApplyEffect;
    }

    public bool UsesContextInput { get; }

    public bool HasDefaultAreaToApplyEffect { get; }
}

public sealed class PortableEffect
{
    private PortableEffect(
        PortableEffectKind kind,
        double radius,
        double blurRadius,
        double shadowDepth,
        double direction,
        double opacity,
        PortableColor color,
        PortableBlurKernel blurKernel,
        PortableEffectRenderingBias renderingBias)
    {
        Kind = kind;
        Radius = double.IsFinite(radius) ? radius : 0.0;
        BlurRadius = double.IsFinite(blurRadius) ? blurRadius : 0.0;
        ShadowDepth = double.IsFinite(shadowDepth) ? shadowDepth : 0.0;
        Direction = double.IsFinite(direction) ? direction : 0.0;
        Opacity = double.IsFinite(opacity) ? opacity : 1.0;
        Color = color;
        BlurKernel = blurKernel;
        RenderingBias = renderingBias;
    }

    public PortableEffectKind Kind { get; }

    public double Radius { get; }

    public double BlurRadius { get; }

    public double ShadowDepth { get; }

    public double Direction { get; }

    public double Opacity { get; }

    public PortableColor Color { get; }

    public PortableBlurKernel BlurKernel { get; }

    public PortableEffectRenderingBias RenderingBias { get; }

    public static PortableEffect Blur(
        double radius,
        PortableBlurKernel blurKernel = PortableBlurKernel.Gaussian,
        PortableEffectRenderingBias renderingBias =
            PortableEffectRenderingBias.Performance)
    {
        return new PortableEffect(
            PortableEffectKind.Blur,
            radius,
            0.0,
            0.0,
            0.0,
            1.0,
            new PortableColor(255, 0, 0, 0),
            blurKernel,
            renderingBias);
    }

    public static PortableEffect DropShadow(
        double blurRadius,
        double shadowDepth,
        double direction,
        double opacity,
        PortableColor color,
        PortableEffectRenderingBias renderingBias =
            PortableEffectRenderingBias.Performance)
    {
        return new PortableEffect(
            PortableEffectKind.DropShadow,
            0.0,
            blurRadius,
            shadowDepth,
            direction,
            opacity,
            color,
            PortableBlurKernel.Gaussian,
            renderingBias);
    }
}
