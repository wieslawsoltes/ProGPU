using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;

namespace ProGPU.CAD.Native;

public readonly record struct CadNativeMesh3DCamera(
    Matrix4x4 Projection,
    Matrix4x4 View,
    Vector3 Position,
    NativeImageRect ViewportBounds);

public sealed class CadNativeMesh3DSceneOptions
{
    public Vector4 LightDirection { get; init; } = new(0.25f, -0.5f, -1.0f, 1.0f);
    public Vector4 AmbientColor { get; init; } = new(1.0f, 1.0f, 1.0f, 0.25f);
    public Vector4 SpecularColor { get; init; } = new(1.0f, 1.0f, 1.0f, 16.0f);
    public Vector4 MaterialAmbient { get; init; } = new(0.2f, 0.2f, 0.2f, 0.0f);
    public NativeMesh3DRenderMode RenderMode { get; init; } = NativeMesh3DRenderMode.Solid;
    public NativeMesh3DShadingMode ShadingMode { get; init; } =
        NativeMesh3DShadingMode.Flat;

    /// <summary>
    /// Optional CAD policy override. When set, it atomically supplies both
    /// render and shading modes so managed and native viewports cannot select
    /// an invalid visual-style combination.
    /// </summary>
    public CadMesh3DVisualStyle? VisualStyle { get; init; }
}

public readonly record struct CadNativeMesh3DTextureBinding(
    ulong ResourceId,
    ulong Generation,
    IProGpuTextureLeaseSource Source);

/// <summary>
/// Owns the typed same-device texture leases required by one native CAD scene
/// submission. Keep this object alive through submission/fence completion.
/// </summary>
public sealed class CadNativeMesh3DTextureLeaseSet : IDisposable
{
    private readonly IProGpuTextureLease[] _leases;
    private readonly NativeSceneExternalImageBinding[] _bindings;
    private bool _disposed;

    internal CadNativeMesh3DTextureLeaseSet(
        IProGpuTextureLease[] leases,
        NativeSceneExternalImageBinding[] bindings)
    {
        _leases = leases;
        _bindings = bindings;
    }

    public ReadOnlyMemory<NativeSceneExternalImageBinding> Bindings =>
        _bindings;

    public void Bind(NativeCompositor compositor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compositor);
        compositor.BindSceneExternalImages(_bindings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (int index = _leases.Length - 1; index >= 0; index--)
        {
            _leases[index].Dispose();
        }
    }
}

/// <summary>Owns one immutable native scene stream for a CAD 3D mesh generation.</summary>
public sealed class CadNativeMesh3DScene
{
    private readonly byte[] _storage;
    private readonly CadNativeMesh3DTextureBinding[] _textureBindings;

    internal CadNativeMesh3DScene(
        byte[] storage,
        int length,
        ulong sceneId,
        ulong contentGeneration,
        ulong nativeGeneration,
        int drawBatchCount,
        int vertexCount,
        int indexCount,
        CadNativeMesh3DTextureBinding[] textureBindings)
    {
        _storage = storage;
        Length = length;
        SceneId = sceneId;
        ContentGeneration = contentGeneration;
        NativeGeneration = nativeGeneration;
        DrawBatchCount = drawBatchCount;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        _textureBindings = textureBindings;
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
    public ReadOnlyMemory<CadNativeMesh3DTextureBinding> TextureBindings =>
        _textureBindings;

    public CadNativeMesh3DTextureLeaseSet AcquireTextureLeases(
        WgpuContext requiredContext)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        var leases = new IProGpuTextureLease[_textureBindings.Length];
        var bindings = new NativeSceneExternalImageBinding[
            _textureBindings.Length];
        int acquired = 0;
        try
        {
            for (; acquired < _textureBindings.Length; acquired++)
            {
                CadNativeMesh3DTextureBinding binding =
                    _textureBindings[acquired];
                bool success = binding.Source is
                    IProGpuContextTextureLeaseSource contextSource
                        ? contextSource.TryAcquireGpuTextureLease(
                            requiredContext,
                            out leases[acquired])
                        : binding.Source.TryAcquireGpuTextureLease(
                            out leases[acquired]);
                if (!success || leases[acquired] is null)
                {
                    throw new InvalidOperationException(
                        "A retained CAD material texture lease is unavailable.");
                }
                GpuTexture texture = leases[acquired].Texture;
                if (texture.IsDisposed ||
                    !ReferenceEquals(texture.Context, requiredContext))
                {
                    throw new InvalidOperationException(
                        "A retained CAD material texture belongs to a different WebGPU device domain.");
                }
                bindings[acquired] = new NativeSceneExternalImageBinding(
                    binding.ResourceId,
                    binding.Generation,
                    texture);
            }
            return new CadNativeMesh3DTextureLeaseSet(leases, bindings);
        }
        catch
        {
            for (int index = acquired; index >= 0; index--)
            {
                leases[index]?.Dispose();
            }
            throw;
        }
    }
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
    /// <summary>
    /// Encodes a retained CAD mesh generation with the same rebased camera
    /// matrices consumed by the managed <c>Viewport3D</c> path.
    /// </summary>
    public CadNativeMesh3DScene Compile(
        CadRecordedMesh3DScene scene,
        in CadMesh3DViewport viewport,
        float aspectRatio,
        NativeImageRect viewportBounds,
        ulong sceneId,
        CadNativeMesh3DSceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (viewport.RebaseOrigin != scene.RebaseOrigin)
        {
            throw new ArgumentException(
                "The native CAD mesh camera and retained scene must share one rebase origin.",
                nameof(viewport));
        }

        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        var nativeCamera = new CadNativeMesh3DCamera(
            camera.CreateProjectionMatrix(aspectRatio),
            camera.CreateViewMatrix(),
            camera.Position,
            viewportBounds);
        return Compile(scene, nativeCamera, sceneId, options);
    }

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
        (NativeMesh3DRenderMode renderMode,
            NativeMesh3DShadingMode shadingMode) =
            ResolveVisualStyle(options);

        ReadOnlySpan<CadMesh3DDrawBatch> batches = scene.DrawBatches.Span;
        var materialTextureIndices = new int[batches.Length];
        Array.Fill(materialTextureIndices, -1);
        var materialTextureBindings =
            new List<CadNativeMesh3DTextureBinding>();
        var materialTextureLookup =
            new Dictionary<CadMaterialTextureResource, int>();
        for (int index = 0; index < batches.Length; index++)
        {
            CadMesh3DMaterialBinding materialBinding =
                batches[index].MaterialBinding;
            if (materialBinding.TextureResource is not { } resource ||
                materialBinding.TextureSource is not { } source)
            {
                continue;
            }
            if (!materialTextureLookup.TryGetValue(resource, out int slot))
            {
                slot = materialTextureBindings.Count;
                materialTextureLookup.Add(resource, slot);
                materialTextureBindings.Add(new CadNativeMesh3DTextureBinding(
                    checked((ulong)slot + 1U),
                    nativeGeneration,
                    source));
            }
            materialTextureIndices[index] = slot;
        }
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
            CadMesh3DMaterial material = batch.MaterialBinding.Material;
            CadColor32 color = material.DiffuseColor;
            uint? materialImageResourceIndex =
                materialTextureIndices[batchIndex] >= 0
                    ? checked((uint)materialTextureIndices[batchIndex])
                    : null;
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
                new Vector4(
                    material.SpecularColor.Red / 255.0f,
                    material.SpecularColor.Green / 255.0f,
                    material.SpecularColor.Blue / 255.0f,
                    material.Shininess),
                new Vector4(
                    material.AmbientColor.Red / 255.0f,
                    material.AmbientColor.Green / 255.0f,
                    material.AmbientColor.Blue / 255.0f,
                    0.0f),
                opacity: material.Opacity,
                renderMode,
                shadingMode,
                materialImageResourceIndex,
                ToNativeTiling(material.TextureTiling),
                materialImageResourceIndex.HasValue
                    ? material.DiffuseMapBlend
                    : 0.0f,
                material.SelfIllumination);
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
        int resourceCount = hasDraw
            ? checked(materialTextureBindings.Count + 1)
            : 0;
        int storageLength = NativeSceneStreamBuilder.GetRequiredBufferSize(
            hasDraw ? 1 : 0,
            resourceCount,
            arenaCapacity);
        byte[] storage = GC.AllocateUninitializedArray<byte>(storageLength);
        var builder = new NativeSceneStreamBuilder(
            storage,
            sceneId,
            nativeGeneration,
            hasDraw ? 1 : 0,
            resourceCount);
        if (hasDraw)
        {
            for (int index = 0; index < materialTextureBindings.Count; index++)
            {
                CadNativeMesh3DTextureBinding textureBinding =
                    materialTextureBindings[index];
                if (!builder.TryAddExternalImageResource(
                        textureBinding.ResourceId,
                        textureBinding.Generation,
                        out uint textureResourceIndex) ||
                    textureResourceIndex != (uint)index)
                {
                    throw new InvalidOperationException(
                        "The native scene builder rejected a retained CAD material image resource.");
                }
            }
            if (!builder.TryAddMesh3DResource(
                    resourceId: checked(
                        (ulong)materialTextureBindings.Count + 1U),
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
            indexCount,
            materialTextureBindings.ToArray());
    }

    private static NativeMesh3DTextureTiling ToNativeTiling(
        CadMaterialTextureTiling tiling) => tiling switch
        {
            CadMaterialTextureTiling.Tile => NativeMesh3DTextureTiling.Tile,
            CadMaterialTextureTiling.Crop => NativeMesh3DTextureTiling.Crop,
            CadMaterialTextureTiling.Clamp => NativeMesh3DTextureTiling.Clamp,
            _ => NativeMesh3DTextureTiling.None,
        };

    private static (
        NativeMesh3DRenderMode RenderMode,
        NativeMesh3DShadingMode ShadingMode) ResolveVisualStyle(
            CadNativeMesh3DSceneOptions options)
    {
        if (options.VisualStyle is not CadMesh3DVisualStyle visualStyle)
        {
            return (options.RenderMode, options.ShadingMode);
        }

        CadMesh3DVisualStyleState state =
            CadMesh3DVisualStylePolicy.Resolve(visualStyle);
        return (
            state.RenderMode switch
            {
                ProGPU.Scene.Extensions.RenderMode3D.Solid =>
                    NativeMesh3DRenderMode.Solid,
                ProGPU.Scene.Extensions.RenderMode3D.Wireframe =>
                    NativeMesh3DRenderMode.Wireframe,
                ProGPU.Scene.Extensions.RenderMode3D.SolidWireframe =>
                    NativeMesh3DRenderMode.SolidWireframe,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The CAD visual style selected an unknown render mode."),
            },
            state.ShadingMode switch
            {
                ProGPU.Scene.Extensions.ShadingMode3D.Realistic =>
                    NativeMesh3DShadingMode.Realistic,
                ProGPU.Scene.Extensions.ShadingMode3D.Conceptual =>
                    NativeMesh3DShadingMode.Conceptual,
                ProGPU.Scene.Extensions.ShadingMode3D.Flat =>
                    NativeMesh3DShadingMode.Flat,
                ProGPU.Scene.Extensions.ShadingMode3D.HiddenLine =>
                    NativeMesh3DShadingMode.HiddenLine,
                ProGPU.Scene.Extensions.ShadingMode3D.ShadesOfGray =>
                    NativeMesh3DShadingMode.ShadesOfGray,
                ProGPU.Scene.Extensions.ShadingMode3D.XRay =>
                    NativeMesh3DShadingMode.XRay,
                ProGPU.Scene.Extensions.ShadingMode3D.Normals =>
                    NativeMesh3DShadingMode.Normals,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The CAD visual style selected an unknown shading mode."),
            });
    }
}
