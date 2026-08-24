namespace ProGPU.Backend.Native;

public enum NativeMilBackend : byte
{
    WgpuNative,
    Dawn
}

public enum NativeMilResourceType : uint
{
    Visual = 39,
    Viewport3DVisual = 40,
    RenderData = 43,
    RenderTarget = 45,
    HwndRenderTarget = 46,
    GenericRenderTarget = 47,
    MatrixTransform = 66,
    SolidColorBrush = 75,
    Pen = 85
}

public readonly record struct NativeMilMatrix3x2(
    double M11,
    double M12,
    double M21,
    double M22,
    double OffsetX,
    double OffsetY)
{
    public static NativeMilMatrix3x2 Identity { get; } = new(
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0);
}

public readonly record struct NativeMilColor(
    float Red,
    float Green,
    float Blue,
    float Alpha);

public enum NativeMilPenLineCap : uint
{
    Flat,
    Square,
    Round,
    Triangle
}

public enum NativeMilPenLineJoin : uint
{
    Miter,
    Bevel,
    Round
}

public readonly record struct NativeMilPen(
    uint BrushHandle,
    double Thickness,
    NativeMilPenLineCap StartLineCap = NativeMilPenLineCap.Flat,
    NativeMilPenLineCap EndLineCap = NativeMilPenLineCap.Flat,
    NativeMilPenLineCap DashCap = NativeMilPenLineCap.Square,
    NativeMilPenLineJoin LineJoin = NativeMilPenLineJoin.Miter,
    double MiterLimit = 10.0);

public enum NativeMilStatus : uint
{
    Success,
    EndOfBatch,
    InvalidArgument,
    MalformedBatch,
    UnknownCommand,
    UnsupportedCommand,
    DuplicateHandle,
    InvalidHandle,
    InvalidResourceType,
    ResourceTypeMismatch,
    InvalidGraph,
    CapacityExceeded
}

public readonly record struct NativeMilBatchMetrics(
    uint CommandCount,
    uint SupportedCommandCount,
    uint UnsupportedCommandCount,
    uint CreatedResourceCount,
    uint DeletedResourceCount,
    uint UpdatedResourceCount,
    uint TotalBytes);

public readonly record struct NativeMilVisualSnapshot(
    uint Handle,
    double OffsetX,
    double OffsetY,
    double Opacity,
    uint ContentHandle,
    uint ChildCount);

public readonly record struct NativeMilTargetSnapshot(
    uint Handle,
    uint RootHandle,
    float ClearRed,
    float ClearGreen,
    float ClearBlue,
    float ClearAlpha,
    uint Flags);

public readonly record struct NativeMilSceneMetrics(
    uint VisualCount,
    uint RectangleCount,
    uint EllipseCount,
    uint RoundedRectangleCount,
    uint LineCount,
    uint BrushCount,
    uint MaximumVisualDepth,
    ulong StreamBytes);

public sealed record NativeMilCompiledScene(
    byte[] Stream,
    NativeMilSceneMetrics Metrics);

public sealed class NativeMilException : Exception
{
    public NativeMilException(NativeMilStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public NativeMilStatus Status { get; }
}
