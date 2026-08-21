namespace ProGPU.Scene.Native;

public enum NativePictureCompileError
{
    None = 0,
    InvalidArgument,
    UnsupportedCommand,
    UnsupportedBrush,
    UnsupportedStroke,
    UnsupportedTransform,
    InvalidState,
    UnbalancedState,
    InvalidGeometry,
    CapacityExceeded,
    StreamBuildFailed
}

public readonly record struct NativePictureCompileFailure(
    NativePictureCompileError Error,
    int CommandIndex,
    RenderCommandType CommandType)
{
    public static NativePictureCompileFailure None => new(
        NativePictureCompileError.None,
        -1,
        default);
}
