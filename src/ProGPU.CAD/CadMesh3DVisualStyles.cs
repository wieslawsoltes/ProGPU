using ProGPU.Scene.Extensions;

namespace ProGPU.CAD;

/// <summary>
/// Host-neutral CAD viewport presentation modes for retained 3D surfaces.
/// </summary>
public enum CadMesh3DVisualStyle : byte
{
    Wireframe = 0,
    Hidden = 1,
    Realistic = 2,
    Conceptual = 3,
    Shaded = 4,
    ShadedWithEdges = 5,
    ShadesOfGray = 6,
    XRay = 7,
    Normals = 8,
}

/// <summary>
/// Exact managed Mesh3D pipeline state selected by one CAD visual style.
/// </summary>
public readonly record struct CadMesh3DVisualStyleState(
    RenderMode3D RenderMode,
    ShadingMode3D ShadingMode);

/// <summary>
/// Maps CAD visual styles to the canonical ProGPU Mesh3D render contract.
/// </summary>
/// <remarks>
/// This original ProGPU policy uses the existing ProGPU-owned
/// <c>RenderMode3D</c>, <c>ShadingMode3D</c>, Mesh3DSolid.wgsl, and
/// Mesh3DWireframe.wgsl implementation as its exact in-repository provenance.
/// Resolution is bounded O(1) time and storage and never changes retained
/// geometry or camera state.
/// </remarks>
public static class CadMesh3DVisualStylePolicy
{
    public static CadMesh3DVisualStyleState Resolve(
        CadMesh3DVisualStyle visualStyle) => visualStyle switch
    {
        CadMesh3DVisualStyle.Wireframe => new(
            RenderMode3D.Wireframe,
            ShadingMode3D.Flat),
        CadMesh3DVisualStyle.Hidden => new(
            RenderMode3D.SolidWireframe,
            ShadingMode3D.HiddenLine),
        CadMesh3DVisualStyle.Realistic => new(
            RenderMode3D.Solid,
            ShadingMode3D.Realistic),
        CadMesh3DVisualStyle.Conceptual => new(
            RenderMode3D.SolidWireframe,
            ShadingMode3D.Conceptual),
        CadMesh3DVisualStyle.Shaded => new(
            RenderMode3D.Solid,
            ShadingMode3D.Realistic),
        CadMesh3DVisualStyle.ShadedWithEdges => new(
            RenderMode3D.SolidWireframe,
            ShadingMode3D.Realistic),
        CadMesh3DVisualStyle.ShadesOfGray => new(
            RenderMode3D.Solid,
            ShadingMode3D.ShadesOfGray),
        CadMesh3DVisualStyle.XRay => new(
            RenderMode3D.SolidWireframe,
            ShadingMode3D.XRay),
        CadMesh3DVisualStyle.Normals => new(
            RenderMode3D.Solid,
            ShadingMode3D.Normals),
        _ => throw new ArgumentOutOfRangeException(
            nameof(visualStyle),
            visualStyle,
            "The CAD Mesh3D visual style is outside the supported range."),
    };
}
