using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public enum NativeRendererStatus : uint
{
    Success = 0,
    InvalidArgument = 1,
    Unsupported = 2,
    OutOfMemory = 3,
    WrongThread = 4,
    DeviceLost = 5,
    InternalError = 6
}

public enum NativeRendererTextureFormat : uint
{
    Rgba8Unorm = 1,
    Bgra8Unorm = 2,
    Rgba8UnormSrgb = 3,
    Bgra8UnormSrgb = 4
}

public enum NativeAnalyticPrimitiveKind : uint
{
    Rectangle = 0,
    Ellipse = 1,
    RoundedRectangle = 2
}

public enum NativeGeometryPrimitiveKind : uint
{
    Line = 0,
    Triangle = 1,
    Quadrilateral = 2
}

[Flags]
public enum NativeAnalyticPrimitiveFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0
}

[Flags]
public enum NativeGeometryPrimitiveFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0,
    Hairline = 1U << 1,
    FixedDeviceStroke = 1U << 2
}

[Flags]
public enum NativeRendererCapabilities : ulong
{
    None = 0,
    SolidRectBatch = 1UL << 0,
    SharedVectorShader = 1UL << 1,
    ExternalTarget = 1UL << 2,
    IndexedAnalyticBatch = 1UL << 3,
    Affine2D = 1UL << 4,
    IndexedGeometryBatch = 1UL << 5,
    DeviceStrokes = 1UL << 6
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSolidRectangle
{
    public NativeSolidRectangle(
        float x,
        float y,
        float width,
        float height,
        Vector4 color)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Color = color;
    }

    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;
    public readonly Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeAnalyticPrimitive
{
    public NativeAnalyticPrimitive(
        NativeAnalyticPrimitiveKind kind,
        float x,
        float y,
        float width,
        float height,
        Vector4 color,
        Matrix3x2 transform,
        float cornerRadius = 0f,
        float strokeThickness = 0f,
        NativeAnalyticPrimitiveFlags flags = NativeAnalyticPrimitiveFlags.None)
    {
        Kind = kind;
        Flags = flags;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        CornerRadius = cornerRadius;
        StrokeThickness = strokeThickness;
        Color = color;
        Transform = transform;
    }

    public readonly NativeAnalyticPrimitiveKind Kind;
    public readonly NativeAnalyticPrimitiveFlags Flags;
    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;
    public readonly float CornerRadius;
    public readonly float StrokeThickness;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeGeometryPrimitive
{
    public NativeGeometryPrimitive(
        NativeGeometryPrimitiveKind kind,
        Vector2 p0,
        Vector2 p1,
        Vector4 color,
        Matrix3x2 transform,
        Vector2 p2 = default,
        Vector2 p3 = default,
        float strokeThickness = 0f,
        NativeGeometryPrimitiveFlags flags = NativeGeometryPrimitiveFlags.None)
    {
        Kind = kind;
        Flags = flags;
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
        StrokeThickness = strokeThickness;
        Reserved = 0f;
        Color = color;
        Transform = transform;
    }

    public readonly NativeGeometryPrimitiveKind Kind;
    public readonly NativeGeometryPrimitiveFlags Flags;
    public readonly Vector2 P0;
    public readonly Vector2 P1;
    public readonly Vector2 P2;
    public readonly Vector2 P3;
    public readonly float StrokeThickness;
    private readonly float Reserved;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
}

public readonly record struct NativeFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    ulong VertexUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeAnalyticFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeGeometryFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong BrushUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeRendererInfo(
    uint AbiVersion,
    uint BackendAbi,
    NativeRendererCapabilities Capabilities,
    string Name);
