using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;

namespace ProGPU.CAD.Native;

public readonly record struct CadNativeMesh3DCamera(
    Matrix4x4 Projection,
    Matrix4x4 View,
    Vector3 Position,
    NativeImageRect ViewportBounds);

public sealed class CadNativeMesh3DSceneOptions
{
    public Vector4 LightDirection { get; init; } = new(0.25f, -0.5f, -1.0f, 0.0f);
    public Vector4 AmbientColor { get; init; } = new(1.0f, 1.0f, 1.0f, 0.25f);
    public Vector4 SpecularColor { get; init; } = new(1.0f, 1.0f, 1.0f, 16.0f);
    public Vector4 MaterialAmbient { get; init; } = new(0.2f, 0.2f, 0.2f, 0.0f);
    public NativeMesh3DRenderMode RenderMode { get; init; } = NativeMesh3DRenderMode.Solid;
}

/// <summary>Owns one immutable native scene stream for a CAD 3D mesh generation.</summary>
public sealed class CadNativeMesh3DScene
{
    private readonly byte[] _storage;

    internal CadNativeMesh3DScene(
        byte[] storage,
        int length,
        ulong sceneId,
        ulong contentGeneration,
        ulong nativeGeneration,
        int drawBatchCount,
        int vertexCount,
        int indexCount)
    {
        _storage = storage;
        Length = length;
        SceneId = sceneId;
        ContentGeneration = contentGeneration;
        NativeGeneration = nativeGeneration;
        DrawBatchCount = drawBatchCount;
        VertexCount = vertexCount;
        IndexCount = indexCount;
    }

    public int Length { get; }
    public ulong SceneId { get; }
    public ulong ContentGeneration { get; }
    /// <summary>
    /// Gets the nonzero ABI generation. It is the CAD generation plus one so
    /// an unchanged newly-created document at generation zero remains valid.
    /// </summary>
    public ulong NativeGeneration { get; }
    public int DrawBatchCount { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public ReadOnlyMemory<byte> Memory => _storage.AsMemory(0, Length);
    public ReadOnlySpan<byte> Stream => _storage.AsSpan(0, Length);
}

/// <summary>
/// Encodes a camera-independent CAD mesh scene as one native Mesh3D resource
/// and one draw command using the existing shared Native3D shader contract.
/// </summary>
/// <remarks>
/// Compilation is O(B + V + I) time and storage for B style batches, V flat
/// vertices, and I triangle indices. The resulting stream is pointer-free and
/// requires one scene update when its generation changes plus one render call
/// per frame; it introduces no per-face or per-triangle native crossing.
/// </remarks>
public sealed class CadNativeMesh3DSceneCompiler
{
    public CadNativeMesh3DScene Compile(
        CadRecordedMesh3DScene scene,
        in CadNativeMesh3DCamera camera,
        ulong sceneId,
        CadNativeMesh3DSceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfZero(sceneId);
        if (scene.ContentGeneration == ulong.MaxValue)
        {
            throw new ArgumentException(
                "The CAD content generation cannot be mapped to the nonzero native ABI range.",
                nameof(scene));
        }
        ulong nativeGeneration = scene.ContentGeneration + 1U;
        options ??= new CadNativeMesh3DSceneOptions();

        ReadOnlySpan<CadMesh3DDrawBatch> batches = scene.DrawBatches.Span;
        int vertexCount = 0;
        int indexCount = 0;
        for (int i = 0; i < batches.Length; i++)
        {
            vertexCount = checked(vertexCount + batches[i].Positions.Length);
            indexCount = checked(indexCount + batches[i].Indices.Length);
        }

        var nativeMeshes = new NativeSceneMesh3D[batches.Length];
        var nativeVertices = new NativeSceneMesh3DVertex[vertexCount];
        var nativeIndices = new uint[indexCount];
        int vertexOffset = 0;
        int indexOffset = 0;
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            CadMesh3DDrawBatch batch = batches[batchIndex];
            ReadOnlySpan<Vector3> positions = batch.Positions.Span;
            ReadOnlySpan<Vector3> normals = batch.Normals.Span;
            ReadOnlySpan<Vector2> textureCoordinates = batch.TextureCoordinates.Span;
            if (positions.Length != normals.Length ||
                positions.Length != textureCoordinates.Length)
            {
                throw new InvalidOperationException(
                    "A CAD mesh batch has mismatched vertex attribute streams.");
            }
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                nativeVertices[vertexOffset + vertex] = new NativeSceneMesh3DVertex(
                    positions[vertex],
                    normals[vertex],
                    textureCoordinates[vertex]);
            }
            batch.Indices.Span.CopyTo(nativeIndices.AsSpan(indexOffset));
            CadColor32 color = batch.Color;
            nativeMeshes[batchIndex] = new NativeSceneMesh3D(
                checked((uint)vertexOffset),
                checked((uint)positions.Length),
                checked((uint)indexOffset),
                checked((uint)batch.Indices.Length),
                new Vector4(
                    color.Red / 255.0f,
                    color.Green / 255.0f,
                    color.Blue / 255.0f,
                    color.Alpha / 255.0f),
                options.LightDirection,
                options.AmbientColor,
                options.SpecularColor,
                options.MaterialAmbient,
                opacity: 1.0f,
                options.RenderMode,
                shadingMode: 2U);
            vertexOffset += positions.Length;
            indexOffset += batch.Indices.Length;
        }

        bool hasDraw = batches.Length != 0;
        int arenaCapacity = hasDraw
            ? checked(
                (nativeMeshes.Length * Unsafe.SizeOf<NativeSceneMesh3D>()) +
                (nativeVertices.Length * Unsafe.SizeOf<NativeSceneMesh3DVertex>()) +
                (nativeIndices.Length * sizeof(uint)) +
                Unsafe.SizeOf<NativeSceneCamera3D>() + 32)
            : 0;
        int storageLength = NativeSceneStreamBuilder.GetRequiredBufferSize(
            hasDraw ? 1 : 0,
            hasDraw ? 1 : 0,
            arenaCapacity);
        byte[] storage = GC.AllocateUninitializedArray<byte>(storageLength);
        var builder = new NativeSceneStreamBuilder(
            storage,
            sceneId,
            nativeGeneration,
            hasDraw ? 1 : 0,
            hasDraw ? 1 : 0);
        if (hasDraw)
        {
            if (!builder.TryAddMesh3DResource(
                    resourceId: 1U,
                    nativeGeneration,
                    nativeMeshes,
                    nativeVertices,
                    nativeIndices,
                    out uint resourceIndex))
            {
                throw new InvalidOperationException(
                    "The native scene builder rejected the retained CAD mesh resource.");
            }
            var nativeCamera = new NativeSceneCamera3D(
                camera.Projection,
                camera.View,
                camera.Position);
            if (!builder.TryDrawMesh3D(
                    commandId: 1U,
                    resourceIndex,
                    camera.ViewportBounds,
                    nativeCamera))
            {
                throw new InvalidOperationException(
                    "The native scene builder rejected the CAD mesh draw command.");
            }
        }
        if (!builder.TryBuild(out ReadOnlySpan<byte> stream))
        {
            throw new InvalidOperationException(
                "The native scene builder could not finalize the CAD mesh stream.");
        }

        return new CadNativeMesh3DScene(
            storage,
            stream.Length,
            sceneId,
            scene.ContentGeneration,
            nativeGeneration,
            batches.Length,
            vertexCount,
            indexCount);
    }
}
