using System.Numerics;

namespace ProGPU.Scene;

/// <summary>
/// Immutable state used by framework hosts that record directly into a
/// ProGPU retained drawing context. The outer transform is composed after
/// command-local transforms and is never applied to decoded pixel storage.
/// </summary>
public readonly struct ProGpuDrawingContextState
{
    public ProGpuDrawingContextState(
        DrawingContext drawingContext,
        Matrix4x4 outerTransform)
    {
        DrawingContext =
            drawingContext ??
            throw new ArgumentNullException(
                nameof(drawingContext));
        if (!IsFinite(outerTransform))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outerTransform));
        }

        OuterTransform = outerTransform;
    }

    public DrawingContext DrawingContext { get; }

    public Matrix4x4 OuterTransform { get; }

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) &&
        float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) &&
        float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) &&
        float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) &&
        float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) &&
        float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) &&
        float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) &&
        float.IsFinite(value.M44);
}

/// <summary>
/// Typed, reflection-free bridge implemented by framework drawing surfaces
/// that can accept retained ProGPU commands. Querying the state is O(1) and
/// allocation-free.
/// </summary>
public interface IProGpuDrawingContextSource
{
    bool TryGetProGpuDrawingContext(
        out ProGpuDrawingContextState state);
}
