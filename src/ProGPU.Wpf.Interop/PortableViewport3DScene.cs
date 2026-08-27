namespace ProGPU.Wpf.Interop;

public interface IPortableViewport3DSceneSource
{
    bool TryGetPortableViewport3DScene(out PortableViewport3DScene scene);
}

public enum PortableViewport3DCameraKind
{
    Perspective = 0,
    Orthographic = 1
}

public enum PortableViewport3DLightKind
{
    Ambient = 0,
    Directional = 1,
    Point = 2,
    Spot = 3
}

public readonly struct PortableVector3
{
    public PortableVector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }
}

public readonly struct PortableColor4
{
    public PortableColor4(double r, double g, double b, double a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public double R { get; }

    public double G { get; }

    public double B { get; }

    public double A { get; }
}

public readonly struct PortableMatrix4x4
{
    public PortableMatrix4x4(
        double m11,
        double m12,
        double m13,
        double m14,
        double m21,
        double m22,
        double m23,
        double m24,
        double m31,
        double m32,
        double m33,
        double m34,
        double m41,
        double m42,
        double m43,
        double m44)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M14 = m14;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M24 = m24;
        M31 = m31;
        M32 = m32;
        M33 = m33;
        M34 = m34;
        M41 = m41;
        M42 = m42;
        M43 = m43;
        M44 = m44;
    }

    public static PortableMatrix4x4 Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public double M11 { get; }

    public double M12 { get; }

    public double M13 { get; }

    public double M14 { get; }

    public double M21 { get; }

    public double M22 { get; }

    public double M23 { get; }

    public double M24 { get; }

    public double M31 { get; }

    public double M32 { get; }

    public double M33 { get; }

    public double M34 { get; }

    public double M41 { get; }

    public double M42 { get; }

    public double M43 { get; }

    public double M44 { get; }
}

public sealed class PortableViewport3DCamera
{
    public PortableViewport3DCameraKind Kind { get; set; }

    public PortableVector3 Position { get; set; }

    public PortableVector3 LookDirection { get; set; }

    public PortableVector3 UpDirection { get; set; }

    public double NearPlaneDistance { get; set; }

    public double FarPlaneDistance { get; set; }

    public double FieldOfView { get; set; }

    public double Width { get; set; }

    public bool HasTransform { get; set; }

    public PortableMatrix4x4 Transform { get; set; } = PortableMatrix4x4.Identity;
}

public sealed class PortableViewport3DMesh
{
    private static readonly PortableVector3[] s_emptyVectors = System.Array.Empty<PortableVector3>();
    private static readonly int[] s_emptyIndices = System.Array.Empty<int>();

    public object? Geometry { get; set; }

    public int GeometryVersion { get; set; }

    public PortableVector3[] Positions { get; set; } = s_emptyVectors;

    public PortableVector3[] Normals { get; set; } = s_emptyVectors;

    public int[] Indices { get; set; } = s_emptyIndices;

    public PortableMatrix4x4 ModelTransform { get; set; } = PortableMatrix4x4.Identity;

    public PortableColor4 DiffuseColor { get; set; } = new(1, 1, 1, 1);

    public PortableColor4 SpecularColor { get; set; } = new(0.2, 0.2, 0.2, 1);

    public double Shininess { get; set; } = 32.0;

    public PortableVector3 AmbientColor { get; set; } = new(0.2, 0.2, 0.2);

    public double Opacity { get; set; } = 1.0;

    public bool IsBackFace { get; set; }
}

public sealed class PortableViewport3DLight
{
    public PortableViewport3DLightKind Kind { get; set; }

    public PortableColor4 Color { get; set; } = new(1, 1, 1, 1);

    public PortableVector3 Position { get; set; }

    public PortableVector3 Direction { get; set; } = new(0, 0, -1);

    public double Range { get; set; } = double.PositiveInfinity;

    public double ConstantAttenuation { get; set; } = 1.0;

    public double LinearAttenuation { get; set; }

    public double QuadraticAttenuation { get; set; }

    public double OuterConeAngle { get; set; } = 90.0;

    public double InnerConeAngle { get; set; } = 180.0;
}

public sealed class PortableViewport3DScene
{
    private static readonly PortableViewport3DMesh[] s_emptyMeshes = System.Array.Empty<PortableViewport3DMesh>();
    private static readonly PortableViewport3DLight[] s_emptyLights = System.Array.Empty<PortableViewport3DLight>();

    public PortableRect Viewport { get; set; } = PortableRect.Empty;

    public PortableViewport3DCamera? Camera { get; set; }

    public PortableVector3 LightDirection { get; set; } = new(0.5, 1.0, -0.5);

    public double LightIntensity { get; set; } = 1.0;

    public PortableVector3 AmbientColor { get; set; } = new(1.0, 1.0, 1.0);

    public double AmbientIntensity { get; set; } = 0.2;

    public PortableViewport3DLight[] Lights { get; set; } = s_emptyLights;

    public PortableViewport3DMesh[] Meshes { get; set; } = s_emptyMeshes;
}
