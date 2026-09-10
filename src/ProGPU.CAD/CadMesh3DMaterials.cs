using ProGPU.Backend;

namespace ProGPU.CAD;

/// <summary>Supported retained diffuse-map addressing policy.</summary>
public enum CadMaterialTextureTiling : byte
{
    None = 0,
    Tile = 1,
    Crop = 2,
    Clamp = 3,
}

public enum CadMaterialTextureProjection : byte
{
    None = 0,
    Planar = 1,
    Box = 2,
    Cylinder = 3,
    Sphere = 4,
}

/// <summary>
/// Immutable host-resolved diffuse image identity. The snapshot never reads the
/// file or owns decoded pixels/GPU resources.
/// </summary>
public readonly record struct CadMaterialTextureResource(
    ulong MaterialHandle,
    string FileName);

/// <summary>Immutable material state consumed by both retained 3D backends.</summary>
public readonly record struct CadMesh3DMaterial(
    string Name,
    CadColor32 DiffuseColor,
    CadColor32 AmbientColor,
    CadColor32 SpecularColor,
    float Opacity,
    float Shininess,
    float SelfIllumination,
    float DiffuseMapBlend,
    CadMaterialTextureProjection TextureProjection,
    CadMaterialTextureTiling TextureTiling,
    bool ScaleMapperToEntityExtents,
    System.Numerics.Matrix4x4 TextureTransform,
    int TextureResourceIndex)
{
    public bool HasDiffuseTexture => TextureResourceIndex >= 0;
}

/// <summary>Immutable diffuse-map resolution request.</summary>
public readonly record struct CadMaterialTextureRequest(
    string? DocumentSourceName,
    CadMaterialTextureResource Resource);

/// <summary>
/// Maps retained CAD material image identity to a typed texture lease source.
/// Implementations must not perform file/network I/O during scene compilation
/// or replay.
/// </summary>
public interface ICadMaterialTextureSourceResolver
{
    bool TryResolve(
        in CadMaterialTextureRequest request,
        out IProGpuTextureLeaseSource source);
}

/// <summary>One resolved retained material plus its optional typed texture.</summary>
public readonly record struct CadMesh3DMaterialBinding(
    CadMesh3DMaterial Material,
    CadMaterialTextureResource? TextureResource,
    IProGpuTextureLeaseSource? TextureSource)
{
    public bool HasResolvedTexture => TextureSource is not null;
}
