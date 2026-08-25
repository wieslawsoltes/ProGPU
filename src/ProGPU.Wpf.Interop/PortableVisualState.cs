namespace ProGPU.Wpf.Interop;

public interface IPortableVisualStateSource
{
    bool TryGetPortableVisualState(out PortableVisualState state);
}

public sealed class PortableVisualState
{
    private static readonly double[] s_emptyGuidelines = System.Array.Empty<double>();

    public bool HasOffset { get; set; }

    public PortablePoint Offset { get; set; }

    public bool HasTransform { get; set; }

    public object? Transform { get; set; }

    public bool HasClip { get; set; }

    public object? Clip { get; set; }

    public bool HasScrollableAreaClip { get; set; }

    public PortableRect ScrollableAreaClip { get; set; }

    public bool HasOpacity { get; set; }

    public double Opacity { get; set; } = 1.0;

    public bool HasOpacityMask { get; set; }

    public object? OpacityMask { get; set; }

    public bool HasEffect { get; set; }

    public object? Effect { get; set; }

    public bool HasBitmapEffect { get; set; }

    public object? BitmapEffect { get; set; }

    public bool HasBitmapEffectInput { get; set; }

    public object? BitmapEffectInput { get; set; }

    public bool HasCacheMode { get; set; }

    public object? CacheMode { get; set; }

    public bool HasBitmapScalingMode { get; set; }

    public object? BitmapScalingMode { get; set; }

    public bool HasPortableBitmapScalingMode { get; set; }

    public PortableBitmapScalingMode PortableBitmapScalingMode { get; set; }

    public bool HasEdgeMode { get; set; }

    public object? EdgeMode { get; set; }

    public bool HasPortableEdgeMode { get; set; }

    public PortableEdgeMode PortableEdgeMode { get; set; }

    public bool HasClearTypeHint { get; set; }

    public object? ClearTypeHint { get; set; }

    public bool HasPortableClearTypeHint { get; set; }

    public PortableClearTypeHint PortableClearTypeHint { get; set; }

    public bool HasTextRenderingMode { get; set; }

    public object? TextRenderingMode { get; set; }

    public bool HasPortableTextRenderingMode { get; set; }

    public PortableTextRenderingMode PortableTextRenderingMode { get; set; }

    public bool HasTextHintingMode { get; set; }

    public object? TextHintingMode { get; set; }

    public bool HasPortableTextHintingMode { get; set; }

    public PortableTextHintingMode PortableTextHintingMode { get; set; }

    public bool HasSnappingGuidelinesX { get; set; }

    public double[] SnappingGuidelinesX { get; set; } = s_emptyGuidelines;

    public bool HasSnappingGuidelinesY { get; set; }

    public double[] SnappingGuidelinesY { get; set; } = s_emptyGuidelines;
}
