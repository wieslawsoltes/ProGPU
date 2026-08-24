namespace ProGPU.Backend.Native;

public enum NativeMilBackend : byte
{
    WgpuNative,
    Dawn
}

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
    InvalidGraph
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

public sealed class NativeMilException : Exception
{
    public NativeMilException(NativeMilStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public NativeMilStatus Status { get; }
}
