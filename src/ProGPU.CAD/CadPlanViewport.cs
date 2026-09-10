using System.Numerics;

namespace ProGPU.CAD;

/// <summary>
/// Shared double-world/float-screen mapping for the retained WCS-XY plan view.
/// </summary>
/// <remarks>
/// The immutable picture stores coordinates relative to RebaseOrigin. Pan and zoom
/// remain view state, so camera-only interaction never recompiles document geometry.
/// Mapping and matrix creation are allocation-free O(1) operations.
/// </remarks>
public readonly record struct CadPlanViewport
{
    public CadPoint3D RebaseOrigin { get; }

    public Vector2 ViewportSize { get; }

    public Vector2 Pan { get; }

    public float Zoom { get; }

    public CadPlanViewport(
        CadPoint3D rebaseOrigin,
        Vector2 viewportSize,
        Vector2 pan,
        float zoom)
    {
        if (!IsFinite(rebaseOrigin))
        {
            throw new ArgumentException("The plan-view rebase origin must be finite.", nameof(rebaseOrigin));
        }
        if (!IsFinite(viewportSize) || viewportSize.X < 0.0f || viewportSize.Y < 0.0f)
        {
            throw new ArgumentException("The plan-view size must be finite and non-negative.", nameof(viewportSize));
        }
        if (!IsFinite(pan))
        {
            throw new ArgumentException("The plan-view pan must be finite.", nameof(pan));
        }
        if (!float.IsFinite(zoom) || zoom <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "Plan-view zoom must be finite and positive.");
        }
        if (!float.IsFinite((viewportSize.X * 0.5f) + pan.X) ||
            !float.IsFinite((viewportSize.Y * 0.5f) + pan.Y))
        {
            throw new ArgumentException("The plan-view center and pan must produce a finite translation.");
        }

        RebaseOrigin = rebaseOrigin;
        ViewportSize = viewportSize;
        Pan = pan;
        Zoom = zoom;
    }

    public Matrix4x4 CreateCameraMatrix()
    {
        EnsureInitialized();
        return new Matrix4x4(
            Zoom, 0, 0, 0,
            0, -Zoom, 0, 0,
            0, 0, 1, 0,
            (ViewportSize.X * 0.5f) + Pan.X,
            (ViewportSize.Y * 0.5f) + Pan.Y,
            0, 1);
    }

    /// <summary>
    /// Replaces the double-precision world origin without changing any projected
    /// WCS-XY position.
    /// </summary>
    /// <remarks>
    /// Snapshot recompilation may choose a different large-coordinate rebase.
    /// Compensating pan is O(1), allocation-free, and keeps camera state independent
    /// of document-generation changes.
    /// </remarks>
    public CadPlanViewport WithRebaseOrigin(CadPoint3D rebaseOrigin)
    {
        EnsureInitialized();
        if (!IsFinite(rebaseOrigin))
        {
            throw new ArgumentException(
                "The replacement plan-view rebase origin must be finite.",
                nameof(rebaseOrigin));
        }

        double deltaX = (rebaseOrigin.X - RebaseOrigin.X) * Zoom;
        double deltaY = (rebaseOrigin.Y - RebaseOrigin.Y) * Zoom;
        double panX = Pan.X + deltaX;
        double panY = Pan.Y - deltaY;
        if (!double.IsFinite(panX) ||
            !double.IsFinite(panY) ||
            panX < float.MinValue ||
            panX > float.MaxValue ||
            panY < float.MinValue ||
            panY > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rebaseOrigin),
                "The replacement rebase origin exceeds finite plan-view pan coordinates.");
        }

        return new CadPlanViewport(
            rebaseOrigin,
            ViewportSize,
            new Vector2((float)panX, (float)panY),
            Zoom);
    }

    public Vector2 WorldToScreen(CadPoint3D world)
    {
        EnsureInitialized();
        if (!IsFinite(world))
        {
            throw new ArgumentException("The world point must be finite.", nameof(world));
        }

        float localX = (float)(world.X - RebaseOrigin.X);
        float localY = (float)(world.Y - RebaseOrigin.Y);
        if (!float.IsFinite(localX) || !float.IsFinite(localY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(world),
                "The rebased world point exceeds finite plan-view coordinates.");
        }
        Vector2 screen = new(
            (localX * Zoom) + (ViewportSize.X * 0.5f) + Pan.X,
            (-localY * Zoom) + (ViewportSize.Y * 0.5f) + Pan.Y);
        if (!IsFinite(screen))
        {
            throw new ArgumentOutOfRangeException(
                nameof(world),
                "The projected world point exceeds finite screen coordinates.");
        }
        return screen;
    }

    public CadPoint3D ScreenToWorld(Vector2 screen, double z = 0.0)
    {
        EnsureInitialized();
        if (!IsFinite(screen) || !double.IsFinite(z))
        {
            throw new ArgumentException("The screen point and world Z must be finite.", nameof(screen));
        }

        Vector2 center = ViewportSize * 0.5f;
        Vector2 local = (screen - center - Pan) / Zoom;
        return new CadPoint3D(
            RebaseOrigin.X + local.X,
            RebaseOrigin.Y - local.Y,
            z);
    }

    /// <summary>Returns the finite WCS-XY window represented by this viewport.</summary>
    public CadBounds3D CreatePlanClipBounds(double z = 0.0)
    {
        EnsureInitialized();
        if (!double.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }
        CadPoint3D first = ScreenToWorld(Vector2.Zero, z);
        CadPoint3D second = ScreenToWorld(ViewportSize, z);
        return new CadBounds3D(
            new CadPoint3D(
                Math.Min(first.X, second.X),
                Math.Min(first.Y, second.Y),
                z),
            new CadPoint3D(
                Math.Max(first.X, second.X),
                Math.Max(first.Y, second.Y),
                z));
    }

    /// <summary>
    /// Converts a screen rectangle into an inclusive world column spanning the
    /// supplied document depth. Inflation is expressed in logical screen pixels.
    /// </summary>
    public CadBounds3D CreateSelectionBounds(
        Vector2 first,
        Vector2 second,
        CadBounds3D documentBounds,
        float inflationPixels = 0.0f)
    {
        EnsureInitialized();
        if (!IsFinite(first) || !IsFinite(second))
        {
            throw new ArgumentException("Selection screen points must be finite.");
        }
        if (!float.IsFinite(inflationPixels) || inflationPixels < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inflationPixels),
                "Selection inflation must be finite and non-negative.");
        }

        double minimumZ = documentBounds.IsEmpty ? 0.0 : documentBounds.Min.Z;
        double maximumZ = documentBounds.IsEmpty ? 0.0 : documentBounds.Max.Z;
        CadPoint3D firstWorld = ScreenToWorld(first, minimumZ);
        CadPoint3D secondWorld = ScreenToWorld(second, maximumZ);
        double inflation = inflationPixels / Zoom;
        return new CadBounds3D(
            new CadPoint3D(
                Math.Min(firstWorld.X, secondWorld.X) - inflation,
                Math.Min(firstWorld.Y, secondWorld.Y) - inflation,
                minimumZ),
            new CadPoint3D(
                Math.Max(firstWorld.X, secondWorld.X) + inflation,
                Math.Max(firstWorld.Y, secondWorld.Y) + inflation,
                maximumZ));
    }

    /// <summary>
    /// Converts a screen rectangle into a WCS-XY selection column spanning every
    /// finite Z value, including unbounded RAY/XLINE projections.
    /// </summary>
    public CadBounds3D CreatePlanSelectionBounds(
        Vector2 first,
        Vector2 second,
        float inflationPixels = 0.0f)
    {
        EnsureInitialized();
        if (!IsFinite(first) || !IsFinite(second))
        {
            throw new ArgumentException("Selection screen points must be finite.");
        }
        if (!float.IsFinite(inflationPixels) || inflationPixels < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inflationPixels),
                "Selection inflation must be finite and non-negative.");
        }

        CadPoint3D firstWorld = ScreenToWorld(first);
        CadPoint3D secondWorld = ScreenToWorld(second);
        double inflation = inflationPixels / Zoom;
        return new CadBounds3D(
            new CadPoint3D(
                Math.Min(firstWorld.X, secondWorld.X) - inflation,
                Math.Min(firstWorld.Y, secondWorld.Y) - inflation,
                -double.MaxValue),
            new CadPoint3D(
                Math.Max(firstWorld.X, secondWorld.X) + inflation,
                Math.Max(firstWorld.Y, secondWorld.Y) + inflation,
                double.MaxValue));
    }

    private void EnsureInitialized()
    {
        if (!float.IsFinite(Zoom) || Zoom <= 0.0f)
        {
            throw new InvalidOperationException("The plan viewport is not initialized.");
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
