using System.Numerics;

namespace ProGPU.CAD;

/// <summary>
/// One finite perspective camera expressed relative to a retained CAD scene's
/// float rebase origin.
/// </summary>
public readonly record struct CadMesh3DProjectionCamera(
    Vector3 Position,
    Vector3 LookDirection,
    Vector3 UpDirection,
    float NearPlaneDistance,
    float FarPlaneDistance,
    float FieldOfView)
{
    public Matrix4x4 CreateViewMatrix() => Matrix4x4.CreateLookAt(
        Position,
        Position + LookDirection,
        UpDirection);

    public Matrix4x4 CreateProjectionMatrix(float aspectRatio)
    {
        if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aspectRatio),
                "The 3D viewport aspect ratio must be finite and positive.");
        }

        return Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfView * (MathF.PI / 180.0f),
            aspectRatio,
            NearPlaneDistance,
            FarPlaneDistance);
    }
}

/// <summary>
/// Double-WCS perspective state for a camera-independent retained CAD mesh
/// scene.
/// </summary>
/// <remarks>
/// Camera mutation and rebase replacement are allocation-free O(1) operations.
/// Geometry compilation is deliberately outside this value. The camera remains
/// in double-precision WCS so a new snapshot origin does not move the user's
/// view; only <see cref="CreateProjectionCamera"/> narrows the position to the
/// float coordinate system consumed by the shared managed/native 3D shaders.
/// </remarks>
public readonly record struct CadMesh3DViewport
{
    private readonly CadPoint3D _positionAnchor;
    private readonly CadPoint3D _positionOffset;

    public CadPoint3D RebaseOrigin { get; }

    public CadPoint3D WorldPosition => _positionAnchor + _positionOffset;

    public CadPoint3D LookDirection { get; }

    public CadPoint3D UpDirection { get; }

    public float NearPlaneDistance { get; }

    public float FarPlaneDistance { get; }

    public float FieldOfView { get; }

    public CadMesh3DViewport(
        CadPoint3D rebaseOrigin,
        CadPoint3D worldPosition,
        CadPoint3D lookDirection,
        CadPoint3D upDirection,
        float nearPlaneDistance,
        float farPlaneDistance,
        float fieldOfView)
    {
        Validate(
            rebaseOrigin,
            worldPosition,
            lookDirection,
            upDirection,
            nearPlaneDistance,
            farPlaneDistance,
            fieldOfView);

        RebaseOrigin = rebaseOrigin;
        _positionAnchor = rebaseOrigin;
        _positionOffset = worldPosition - rebaseOrigin;
        LookDirection = lookDirection;
        UpDirection = upDirection;
        NearPlaneDistance = nearPlaneDistance;
        FarPlaneDistance = farPlaneDistance;
        FieldOfView = fieldOfView;

        _ = CreateProjectionCamera();
    }

    private CadMesh3DViewport(
        CadPoint3D rebaseOrigin,
        CadPoint3D positionAnchor,
        CadPoint3D positionOffset,
        CadPoint3D lookDirection,
        CadPoint3D upDirection,
        float nearPlaneDistance,
        float farPlaneDistance,
        float fieldOfView)
    {
        CadPoint3D worldPosition = positionAnchor + positionOffset;
        Validate(
            rebaseOrigin,
            worldPosition,
            lookDirection,
            upDirection,
            nearPlaneDistance,
            farPlaneDistance,
            fieldOfView);
        if (!IsFinite(positionAnchor) || !IsFinite(positionOffset))
        {
            throw new ArgumentException(
                "The split CAD 3D camera position must be finite.");
        }

        RebaseOrigin = rebaseOrigin;
        _positionAnchor = positionAnchor;
        _positionOffset = positionOffset;
        LookDirection = lookDirection;
        UpDirection = upDirection;
        NearPlaneDistance = nearPlaneDistance;
        FarPlaneDistance = farPlaneDistance;
        FieldOfView = fieldOfView;

        _ = CreateProjectionCamera();
    }

    /// <summary>Creates the established ProGPU.CAD Z-up fitted perspective.</summary>
    public static CadMesh3DViewport Fit(CadRecordedMesh3DScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.DrawBatches.IsEmpty || scene.Bounds.IsEmpty)
        {
            throw new ArgumentException(
                "A fitted 3D viewport requires at least one retained mesh batch and finite bounds.",
                nameof(scene));
        }

        CadBounds3D bounds = scene.Bounds;
        CadPoint3D target = bounds.Center;
        double extent = Math.Max(
            Math.Max(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y),
            bounds.Max.Z - bounds.Min.Z);
        double radius = Math.Max(extent * 1.8, 10.0);
        if (!double.IsFinite(radius) || radius > float.MaxValue / 20.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scene),
                "The retained 3D scene exceeds the finite fitted-camera range.");
        }

        var offset = new CadPoint3D(radius, -radius, radius * 0.8);
        float radiusFloat = (float)radius;
        return new CadMesh3DViewport(
            scene.RebaseOrigin,
            scene.RebaseOrigin,
            (target - scene.RebaseOrigin) + offset,
            offset * -1.0,
            new CadPoint3D(0.0, 0.0, 1.0),
            Math.Max(radiusFloat / 10_000.0f, 0.01f),
            radiusFloat * 20.0f,
            42.0f);
    }

    /// <summary>
    /// Replaces only the scene's float origin while retaining the exact WCS
    /// camera position, orientation, projection, and clipping distances.
    /// </summary>
    public CadMesh3DViewport WithRebaseOrigin(CadPoint3D rebaseOrigin) =>
        new(
            rebaseOrigin,
            _positionAnchor,
            _positionOffset,
            LookDirection,
            UpDirection,
            NearPlaneDistance,
            FarPlaneDistance,
            FieldOfView);

    /// <summary>
    /// Captures one camera-only managed interaction without consulting or
    /// rebuilding retained scene geometry.
    /// </summary>
    public CadMesh3DViewport WithProjectionCamera(
        in CadMesh3DProjectionCamera camera)
    {
        ValidateProjectionCamera(camera);
        CadPoint3D localPosition = ToCadPoint(camera.Position);
        CadPoint3D worldPosition = RebaseOrigin + localPosition;
        if (!IsFinite(worldPosition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(camera),
                "The rebased camera position exceeds finite CAD world coordinates.");
        }

        return new CadMesh3DViewport(
            RebaseOrigin,
            RebaseOrigin,
            localPosition,
            ToCadPoint(camera.LookDirection),
            ToCadPoint(camera.UpDirection),
            camera.NearPlaneDistance,
            camera.FarPlaneDistance,
            camera.FieldOfView);
    }

    public CadMesh3DProjectionCamera CreateProjectionCamera()
    {
        CadPoint3D localPosition =
            (_positionAnchor - RebaseOrigin) + _positionOffset;
        Vector3 position = ToVector3(localPosition, "camera position");
        Vector3 lookDirection = ToVector3(LookDirection, "camera look direction");
        Vector3 upDirection = ToVector3(UpDirection, "camera up direction");
        var result = new CadMesh3DProjectionCamera(
            position,
            lookDirection,
            upDirection,
            NearPlaneDistance,
            FarPlaneDistance,
            FieldOfView);
        ValidateProjectionCamera(result);
        return result;
    }

    private static void Validate(
        CadPoint3D rebaseOrigin,
        CadPoint3D worldPosition,
        CadPoint3D lookDirection,
        CadPoint3D upDirection,
        float nearPlaneDistance,
        float farPlaneDistance,
        float fieldOfView)
    {
        if (!IsFinite(rebaseOrigin) ||
            !IsFinite(worldPosition) ||
            !IsFinite(lookDirection) ||
            !IsFinite(upDirection))
        {
            throw new ArgumentException("CAD 3D camera values must be finite.");
        }
        if (lookDirection.Length <= 0.0 || upDirection.Length <= 0.0)
        {
            throw new ArgumentException(
                "CAD 3D camera look and up directions must be non-zero.");
        }

        CadPoint3D cross = CadPoint3D.Cross(lookDirection, upDirection);
        if (!double.IsFinite(cross.Length) || cross.Length <= 1e-12)
        {
            throw new ArgumentException(
                "CAD 3D camera look and up directions must not be parallel.");
        }
        if (!float.IsFinite(nearPlaneDistance) || nearPlaneDistance <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nearPlaneDistance),
                "The near plane must be finite and positive.");
        }
        if (!float.IsFinite(farPlaneDistance) ||
            farPlaneDistance <= nearPlaneDistance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(farPlaneDistance),
                "The far plane must be finite and greater than the near plane.");
        }
        if (!float.IsFinite(fieldOfView) ||
            fieldOfView <= 0.0f ||
            fieldOfView >= 180.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldOfView),
                "The perspective field of view must be between zero and 180 degrees.");
        }
    }

    private static void ValidateProjectionCamera(
        in CadMesh3DProjectionCamera camera)
    {
        Validate(
            CadPoint3D.Zero,
            ToCadPoint(camera.Position),
            ToCadPoint(camera.LookDirection),
            ToCadPoint(camera.UpDirection),
            camera.NearPlaneDistance,
            camera.FarPlaneDistance,
            camera.FieldOfView);
    }

    private static Vector3 ToVector3(CadPoint3D point, string description)
    {
        if (!IsFinite(point) ||
            point.X < -float.MaxValue || point.X > float.MaxValue ||
            point.Y < -float.MaxValue || point.Y > float.MaxValue ||
            point.Z < -float.MaxValue || point.Z > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(point),
                $"The rebased CAD 3D {description} exceeds finite float coordinates.");
        }

        return new Vector3((float)point.X, (float)point.Y, (float)point.Z);
    }

    private static CadPoint3D ToCadPoint(Vector3 point) =>
        new(point.X, point.Y, point.Z);

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// Monotonic CPU-work counters for the retained CAD 3D view coordinator.
/// </summary>
public readonly record struct CadMesh3DViewStatistics(
    long SceneCompilationCount,
    long SceneReplacementCount,
    long CompiledEntityVisitCount,
    long FittedCameraCount,
    long PreservedCameraCount,
    long CameraUpdateCount,
    long CameraOnlySceneCompilationCount,
    long CameraOnlyEntityVisitCount,
    long CameraOnlyDrawBatchVisitCount,
    long CameraOnlyUploadByteCount);

/// <summary>
/// Owns one immutable mesh generation and independent perspective state for a
/// desktop, browser, or native CAD host.
/// </summary>
/// <remarks>
/// Snapshot replacement performs one O(E + V + I) mesh compilation. Camera
/// capture, rebase preservation, and counter publication are allocation-free
/// O(1): they perform zero entity visits, zero batch visits, zero geometry
/// uploads, and zero managed/native crossings.
/// </remarks>
public sealed class CadMesh3DViewCoordinator
{
    private readonly CadMesh3DSceneCompiler _compiler;
    private CadMesh3DViewStatistics _statistics;

    public CadRecordedMesh3DScene? Scene { get; private set; }

    public CadMesh3DViewport? Viewport { get; private set; }

    public CadMesh3DViewStatistics Statistics => _statistics;

    public CadMesh3DViewCoordinator()
        : this(new CadMesh3DSceneCompiler())
    {
    }

    public CadMesh3DViewCoordinator(CadMesh3DSceneCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        _compiler = compiler;
    }

    public CadRecordedMesh3DScene ReplaceSnapshot(
        CadDocumentSnapshot snapshot,
        bool resetCamera,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CadRecordedMesh3DScene replacement = _compiler.Compile(
            snapshot,
            cancellationToken: cancellationToken);
        bool hasMeshes = !replacement.DrawBatches.IsEmpty;
        bool fit = hasMeshes && (resetCamera || Viewport is null);
        bool preserve = hasMeshes && !fit;

        CadMesh3DViewport? replacementViewport = !hasMeshes
            ? null
            : fit
                ? CadMesh3DViewport.Fit(replacement)
                : Viewport!.Value.WithRebaseOrigin(replacement.RebaseOrigin);

        Scene = replacement;
        Viewport = replacementViewport;
        _statistics = _statistics with
        {
            SceneCompilationCount = checked(
                _statistics.SceneCompilationCount + 1),
            SceneReplacementCount = checked(
                _statistics.SceneReplacementCount + 1),
            CompiledEntityVisitCount = checked(
                _statistics.CompiledEntityVisitCount + snapshot.Entities.Length),
            FittedCameraCount = checked(
                _statistics.FittedCameraCount + (fit ? 1 : 0)),
            PreservedCameraCount = checked(
                _statistics.PreservedCameraCount + (preserve ? 1 : 0)),
        };
        return replacement;
    }

    public void CaptureCamera(in CadMesh3DProjectionCamera camera)
    {
        CadMesh3DViewport viewport = Viewport ?? throw new InvalidOperationException(
            "A retained CAD mesh generation is required before capturing camera state.");
        Viewport = viewport.WithProjectionCamera(camera);
        _statistics = _statistics with
        {
            CameraUpdateCount = checked(_statistics.CameraUpdateCount + 1),
        };
    }

    public CadMesh3DViewport FitCamera()
    {
        CadRecordedMesh3DScene scene = Scene ?? throw new InvalidOperationException(
            "A retained CAD mesh generation is required before fitting its camera.");
        CadMesh3DViewport viewport = CadMesh3DViewport.Fit(scene);
        Viewport = viewport;
        _statistics = _statistics with
        {
            FittedCameraCount = checked(_statistics.FittedCameraCount + 1),
        };
        return viewport;
    }
}
