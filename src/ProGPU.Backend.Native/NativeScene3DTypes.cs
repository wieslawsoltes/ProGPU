using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

public partial struct NativeFloat4
{
    public NativeFloat4(Vector4 value)
    {
        X = value.X;
        Y = value.Y;
        Z = value.Z;
        W = value.W;
    }
}

public partial struct NativePoint3D
{
    public NativePoint3D(Vector3 value)
    {
        X = value.X;
        Y = value.Y;
        Z = value.Z;
        Reserved = 0f;
    }
}

public partial struct NativeMatrix4x4
{
    public NativeMatrix4x4(Matrix4x4 value)
    {
        M11 = value.M11;
        M12 = value.M12;
        M13 = value.M13;
        M14 = value.M14;
        M21 = value.M21;
        M22 = value.M22;
        M23 = value.M23;
        M24 = value.M24;
        M31 = value.M31;
        M32 = value.M32;
        M33 = value.M33;
        M34 = value.M34;
        M41 = value.M41;
        M42 = value.M42;
        M43 = value.M43;
        M44 = value.M44;
    }
}

public partial struct NativeSceneCamera3D
{
    public NativeSceneCamera3D(
        Matrix4x4 projection,
        Matrix4x4 view,
        Vector3 cameraPosition)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneCamera3D>();
        Flags = 0U;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Projection = new NativeMatrix4x4(projection);
        View = new NativeMatrix4x4(view);
        CameraPosition = new NativePoint3D(cameraPosition);
    }
}

public partial struct NativeSceneLine3D
{
    public NativeSceneLine3D(
        Vector3 start,
        Vector3 end,
        Vector4 color,
        float thickness,
        float opacity,
        Matrix4x4 transform)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLine3D>();
        Flags = 0U;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Start = new NativePoint3D(start);
        End = new NativePoint3D(end);
        Color = color;
        Thickness = thickness;
        Opacity = opacity;
        Reserved2 = 0U;
        Reserved3 = 0U;
        Transform = new NativeMatrix4x4(transform);
    }
}

public partial struct NativeSceneMesh3DVertex
{
    public NativeSceneMesh3DVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 textureCoordinate)
    {
        Position = new NativePoint3D(position);
        Normal = new NativePoint3D(normal);
        TextureCoordinate = textureCoordinate;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }
}

public partial struct NativeSceneMesh3D
{
    public NativeSceneMesh3D(
        uint vertexOffset,
        uint vertexCount,
        uint indexOffset,
        uint indexCount,
        Vector4 color,
        Vector4 lightDirection,
        Vector4 ambientColor,
        Vector4 specularColor,
        Vector4 materialAmbient,
        float opacity,
        NativeMesh3DRenderMode renderMode = NativeMesh3DRenderMode.Solid,
        uint shadingMode = 2U)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneMesh3D>();
        Flags = 0U;
        Topology = (uint)NativeMesh3DTopology.Triangles;
        RenderMode = (uint)renderMode;
        VertexOffset = vertexOffset;
        VertexCount = vertexCount;
        IndexOffset = indexOffset;
        IndexCount = indexCount;
        ModelTransform = new NativeMatrix4x4(Matrix4x4.Identity);
        NormalTransform = new NativeMatrix4x4(Matrix4x4.Identity);
        Color = color;
        LightDirection = new NativeFloat4(lightDirection);
        AmbientColor = new NativeFloat4(ambientColor);
        SpecularColor = new NativeFloat4(specularColor);
        MaterialAmbient = new NativeFloat4(materialAmbient);
        Opacity = opacity;
        ShadingMode = shadingMode;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }
}
