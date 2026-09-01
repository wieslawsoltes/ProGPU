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
        uint shadingMode = (uint)NativeMesh3DShadingMode.Flat,
        uint? materialImageResourceIndex = null,
        NativeMesh3DTextureTiling textureTiling =
            NativeMesh3DTextureTiling.None,
        float diffuseMapBlend = 0.0f,
        float selfIllumination = 0.0f)
    {
        if (textureTiling > NativeMesh3DTextureTiling.Clamp)
        {
            throw new ArgumentOutOfRangeException(nameof(textureTiling));
        }
        StructSize = (uint)Unsafe.SizeOf<NativeSceneMesh3D>();
        Flags = !materialImageResourceIndex.HasValue
            ? 0U
            : 1U | ((uint)textureTiling << 1);
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
        MaterialImageResourceIndex = materialImageResourceIndex.GetValueOrDefault();
        MaterialFactors = PackUnitFactors(
            diffuseMapBlend,
            selfIllumination);
    }

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
        NativeMesh3DRenderMode renderMode,
        NativeMesh3DShadingMode shadingMode,
        uint? materialImageResourceIndex = null,
        NativeMesh3DTextureTiling textureTiling =
            NativeMesh3DTextureTiling.None,
        float diffuseMapBlend = 0.0f,
        float selfIllumination = 0.0f)
        : this(
            vertexOffset,
            vertexCount,
            indexOffset,
            indexCount,
            color,
            lightDirection,
            ambientColor,
            specularColor,
            materialAmbient,
            opacity,
            renderMode,
            (uint)shadingMode,
            materialImageResourceIndex,
            textureTiling,
            diffuseMapBlend,
            selfIllumination)
    {
    }

    private static uint PackUnitFactors(float low, float high)
    {
        if (!float.IsFinite(low) || low < 0.0f || low > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(low),
                "The diffuse-map blend factor must be finite and in [0, 1].");
        }
        if (!float.IsFinite(high) || high < 0.0f || high > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(high),
                "The self-illumination factor must be finite and in [0, 1].");
        }
        uint lowBits = (uint)MathF.Round(
            low * ushort.MaxValue);
        uint highBits = (uint)MathF.Round(
            high * ushort.MaxValue);
        return lowBits | (highBits << 16);
    }
}
