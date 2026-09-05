using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// An owned, immutable comparison baseline for a flattened native MIL viewport.
/// </summary>
/// <remarks>
/// Captures every defined wire bit, including reserved fields, rather than array
/// identity or a collision-prone hash. These fixed-layout wire records have no
/// implicit padding (covered by contract tests). A changed reserved field is a
/// change, not permission to bypass the native validator. Capture costs O(B)
/// time/storage; matching is allocation-free O(B) using runtime-intrinsic byte
/// span operations. Callers must not mutate source arrays during either call.
/// </remarks>
public sealed class NativeMilViewport3DSnapshot
{
    private readonly NativeSceneCamera3D _camera;
    private readonly NativeImageRect _viewport;
    private readonly int _meshCount;
    private readonly int _vertexCount;
    private readonly int _indexCount;
    private readonly int _lightCount;
    private readonly int _materialCount;
    private readonly int _stopCount;
    private readonly byte[] _payload;

    private NativeMilViewport3DSnapshot(NativeMilViewport3DScene scene)
    {
        _camera = scene.Camera;
        _viewport = scene.Viewport;
        _meshCount = scene.Meshes.Length;
        _vertexCount = scene.Vertices.Length;
        _indexCount = scene.Indices.Length;
        _lightCount = scene.Lights.Length;
        _materialCount = scene.Materials.Length;
        _stopCount = scene.GradientStops.Length;
        int size = checked(
            _meshCount * Unsafe.SizeOf<NativeSceneMesh3D>() +
            _vertexCount * Unsafe.SizeOf<NativeSceneMesh3DVertex>() +
            _indexCount * sizeof(uint) +
            _lightCount * Unsafe.SizeOf<NativeSceneLight3D>() +
            _materialCount * Unsafe.SizeOf<NativeSceneBrush>() +
            _stopCount * Unsafe.SizeOf<NativeSceneGradientStop>());
        _payload = GC.AllocateUninitializedArray<byte>(size);
        Span<byte> remaining = _payload;
        Copy<NativeSceneMesh3D>(scene.Meshes, ref remaining);
        Copy<NativeSceneMesh3DVertex>(scene.Vertices, ref remaining);
        Copy<uint>(scene.Indices, ref remaining);
        Copy<NativeSceneLight3D>(scene.Lights, ref remaining);
        Copy<NativeSceneBrush>(scene.Materials, ref remaining);
        Copy<NativeSceneGradientStop>(scene.GradientStops, ref remaining);
    }

    /// <summary>Owned payload bytes, excluding the fixed camera and viewport.</summary>
    public int PayloadByteCount => _payload.Length;

    /// <summary>
    /// Copies the comparison baseline. This does not validate or bind a native
    /// scene; retain it as an applied baseline only after a successful binding.
    /// </summary>
    public static NativeMilViewport3DSnapshot Capture(NativeMilViewport3DScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(scene.Meshes);
        ArgumentNullException.ThrowIfNull(scene.Vertices);
        ArgumentNullException.ThrowIfNull(scene.Indices);
        ArgumentNullException.ThrowIfNull(scene.Lights);
        ArgumentNullException.ThrowIfNull(scene.Materials);
        ArgumentNullException.ThrowIfNull(scene.GradientStops);
        return new NativeMilViewport3DSnapshot(scene);
    }

    /// <summary>Compares current caller data against the owned baseline.</summary>
    public bool Matches(NativeMilViewport3DScene? scene)
    {
        if (scene is null || scene.Meshes?.Length != _meshCount ||
            scene.Vertices?.Length != _vertexCount ||
            scene.Indices?.Length != _indexCount ||
            scene.Lights?.Length != _lightCount ||
            scene.Materials?.Length != _materialCount ||
            scene.GradientStops?.Length != _stopCount)
        {
            return false;
        }
        NativeSceneCamera3D camera = scene.Camera;
        NativeImageRect viewport = scene.Viewport;
        ReadOnlySpan<byte> remaining = _payload;
        return BitsEqual(in _camera, in camera) &&
            BitsEqual(in _viewport, in viewport) &&
            Match<NativeSceneMesh3D>(scene.Meshes, ref remaining) &&
            Match<NativeSceneMesh3DVertex>(scene.Vertices, ref remaining) &&
            Match<uint>(scene.Indices, ref remaining) &&
            Match<NativeSceneLight3D>(scene.Lights, ref remaining) &&
            Match<NativeSceneBrush>(scene.Materials, ref remaining) &&
            Match<NativeSceneGradientStop>(scene.GradientStops, ref remaining);
    }

    private static bool BitsEqual<T>(in T left, in T right) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in left, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(in right, 1)));

    private static void Copy<T>(ReadOnlySpan<T> source, ref Span<byte> remaining)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        bytes.CopyTo(remaining);
        remaining = remaining[bytes.Length..];
    }

    private static bool Match<T>(ReadOnlySpan<T> source, ref ReadOnlySpan<byte> remaining)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        bool equal = bytes.SequenceEqual(remaining[..bytes.Length]);
        remaining = remaining[bytes.Length..];
        return equal;
    }
}
