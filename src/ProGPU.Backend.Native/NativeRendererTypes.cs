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

[Flags]
public enum NativeRendererCapabilities : ulong
{
    None = 0,
    SolidRectBatch = 1UL << 0,
    SharedVectorShader = 1UL << 1,
    ExternalTarget = 1UL << 2
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

public readonly record struct NativeFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    ulong VertexUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeRendererInfo(
    uint AbiVersion,
    uint BackendAbi,
    NativeRendererCapabilities Capabilities,
    string Name);
