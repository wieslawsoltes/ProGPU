using System.Numerics;

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Exposes a backend-owned drawing context without making that backend part of
/// the portable WPF contract surface.
/// </summary>
public interface IPortableNativeDrawingContextSource
{
    bool TryGetPortableNativeDrawingContext(out object? nativeDrawingContext);
}

/// <summary>
/// Exposes the backend-owned drawing context together with the transform that
/// portable callers must apply to commands written directly into that context.
/// </summary>
public interface IPortableNativeDrawingContextStateSource
{
    bool TryGetPortableNativeDrawingContextState(out PortableNativeDrawingContextState state);
}

/// <summary>
/// Describes an active backend drawing context without making the backend type
/// part of the portable WPF contract surface.
/// </summary>
public readonly struct PortableNativeDrawingContextState
{
    public PortableNativeDrawingContextState(object nativeDrawingContext, Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(nativeDrawingContext);
        NativeDrawingContext = nativeDrawingContext;
        Transform = transform;
    }

    public object NativeDrawingContext { get; }

    /// <summary>
    /// Gets the current outer transform. Direct writers apply this matrix once
    /// after their own local/client transform.
    /// </summary>
    public Matrix4x4 Transform { get; }
}
