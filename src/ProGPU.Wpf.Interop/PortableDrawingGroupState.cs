namespace ProGPU.Wpf.Interop;

public interface IPortableDrawingGroupStateSource
{
    bool TryGetPortableDrawingGroupState(out PortableDrawingGroupState state);
}

public interface IPortableDrawingGroupChildrenSource
{
    bool TryGetPortableDrawingGroupChildCount(out int count);

    bool TryGetPortableDrawingGroupChild(int index, out object child);
}

public sealed class PortableDrawingGroupState
{
    private static readonly object[] s_emptyChildren = System.Array.Empty<object>();

    public bool HasBounds { get; set; }

    public PortableRect Bounds { get; set; } = PortableRect.Empty;

    /// <summary>
    /// Gets or sets the exact content bounds in the DrawingGroup's local
    /// coordinate space, before the group's own transform is applied.
    /// </summary>
    /// <remarks>
    /// Native retained compositors use this value to size bounded group
    /// isolation and to map spatial opacity masks without applying the group
    /// transform twice. <see cref="Bounds"/> remains the externally visible,
    /// post-transform drawing bounds.
    /// </remarks>
    public bool HasLocalBounds { get; set; }

    public PortableRect LocalBounds { get; set; } = PortableRect.Empty;

    public bool HasTransform { get; set; }

    public object? Transform { get; set; }

    public bool HasClipGeometry { get; set; }

    public object? ClipGeometry { get; set; }

    public bool HasOpacity { get; set; }

    public double Opacity { get; set; } = 1.0;

    public bool HasOpacityMask { get; set; }

    public object? OpacityMask { get; set; }

    public bool HasGuidelineSet { get; set; }

    public object? GuidelineSet { get; set; }

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

    public bool HasTextHintingMode { get; set; }

    public object? TextHintingMode { get; set; }

    public object[] Children { get; set; } = s_emptyChildren;
}
