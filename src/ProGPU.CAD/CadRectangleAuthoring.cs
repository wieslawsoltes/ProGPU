namespace ProGPU.CAD;

/// <summary>The corner construction retained by one RECTANG command.</summary>
public enum CadRectangleCornerMode : byte
{
    Sharp = 0,
    Chamfer = 1,
    Fillet = 2,
}

/// <summary>The known dimension used to solve a RECTANG Area input.</summary>
public enum CadRectangleKnownDimension : byte
{
    Length = 0,
    Width = 1,
}

/// <summary>The final placement solve used by one RECTANG command.</summary>
public enum CadRectangleConstructionMode : byte
{
    DiagonalCorners = 0,
    Dimensions = 1,
    Area = 2,
}

/// <summary>Validated scalar construction retained by one RECTANG session.</summary>
public readonly record struct CadRectangleConstruction
{
    public CadRectangleConstructionMode Mode { get; }

    public double Length { get; }

    public double Width { get; }

    public double Area { get; }

    public CadRectangleKnownDimension KnownDimension { get; }

    public double KnownValue { get; }

    public static CadRectangleConstruction DiagonalCorners => default;

    public static CadRectangleConstruction Dimensions(
        double length,
        double width) => new(
            CadRectangleConstructionMode.Dimensions,
            length,
            width,
            0.0,
            CadRectangleKnownDimension.Length,
            0.0);

    public static CadRectangleConstruction FromArea(
        double area,
        CadRectangleKnownDimension knownDimension,
        double knownValue) => new(
            CadRectangleConstructionMode.Area,
            0.0,
            0.0,
            area,
            knownDimension,
            knownValue);

    private CadRectangleConstruction(
        CadRectangleConstructionMode mode,
        double length,
        double width,
        double area,
        CadRectangleKnownDimension knownDimension,
        double knownValue)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!Enum.IsDefined(knownDimension))
        {
            throw new ArgumentOutOfRangeException(nameof(knownDimension));
        }
        if (mode == CadRectangleConstructionMode.Dimensions &&
            (!IsPositiveRenderable(length) || !IsPositiveRenderable(width)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "RECTANG Dimensions must be finite, positive, and renderable.");
        }
        if (mode == CadRectangleConstructionMode.Area &&
            ((!double.IsFinite(area) || area <= 0.0) ||
             !IsPositiveRenderable(knownValue)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(area),
                "RECTANG Area and its known dimension must be finite and positive; the dimension must be renderable.");
        }

        Mode = mode;
        Length = length;
        Width = width;
        Area = area;
        KnownDimension = knownDimension;
        KnownValue = knownValue;
    }

    private static bool IsPositiveRenderable(double value) =>
        double.IsFinite(value) &&
        value > 0.0 &&
        value <= float.MaxValue;
}

/// <summary>The exact point expected by the current RECTANG prompt.</summary>
public enum CadRectangleAuthoringInputKind : byte
{
    FirstCorner = 0,
    OtherCorner = 1,
}

/// <summary>Validated mutually exclusive RECTANG corner settings.</summary>
public readonly record struct CadRectangleCornerTreatment
{
    public CadRectangleCornerMode Mode { get; }

    /// <summary>
    /// Chamfer distance on local-X edges, or the fillet radius.
    /// </summary>
    public double FirstDistance { get; }

    /// <summary>Chamfer distance on local-Y edges.</summary>
    public double SecondDistance { get; }

    public double FilletRadius =>
        Mode == CadRectangleCornerMode.Fillet ? FirstDistance : 0.0;

    public static CadRectangleCornerTreatment Sharp => default;

    public static CadRectangleCornerTreatment Chamfer(
        double firstDistance,
        double secondDistance) =>
        new(
            CadRectangleCornerMode.Chamfer,
            firstDistance,
            secondDistance);

    public static CadRectangleCornerTreatment Fillet(double radius) =>
        new(CadRectangleCornerMode.Fillet, radius, 0.0);

    public CadRectangleCornerTreatment(
        CadRectangleCornerMode mode,
        double firstDistance,
        double secondDistance)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!double.IsFinite(firstDistance) || firstDistance < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDistance),
                firstDistance,
                "A RECTANG corner distance must be finite and non-negative.");
        }
        if (!double.IsFinite(secondDistance) || secondDistance < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondDistance),
                secondDistance,
                "A RECTANG corner distance must be finite and non-negative.");
        }
        if (mode == CadRectangleCornerMode.Sharp &&
            (firstDistance != 0.0 || secondDistance != 0.0))
        {
            throw new ArgumentException(
                "Sharp RECTANG corners cannot retain chamfer or fillet distances.");
        }
        if (mode == CadRectangleCornerMode.Fillet && secondDistance != 0.0)
        {
            throw new ArgumentException(
                "A RECTANG fillet has one radius and no second distance.",
                nameof(secondDistance));
        }

        Mode = mode;
        FirstDistance = firstDistance;
        SecondDistance = secondDistance;
    }
}

/// <summary>Immutable analytic definition of one plan-view RECTANG result.</summary>
public readonly record struct CadRectangleAuthoringSnapshot
{
    private const double QuarterCircleBulge = 0.4142135623730950488016887242097;

    public CadPoint3D FirstCorner { get; }

    /// <summary>Signed extent along the rotated local-X basis.</summary>
    public double LocalXExtent { get; }

    /// <summary>Signed extent along the rotated local-Y basis.</summary>
    public double LocalYExtent { get; }

    public double RotationRadians { get; }

    public CadRectangleCornerTreatment CornerTreatment { get; }

    public double Length => Math.Abs(LocalXExtent);

    public double Width => Math.Abs(LocalYExtent);

    public double EnclosedArea =>
        (Length * Width) - GetCornerAreaReduction(CornerTreatment);

    public int Orientation => LocalXExtent * LocalYExtent > 0.0 ? 1 : -1;

    public int VertexCount
    {
        get
        {
            Span<CadPoint3D> points = stackalloc CadPoint3D[8];
            Span<double> bulges = stackalloc double[8];
            return BuildVertices(points, bulges);
        }
    }

    public CadRectangleAuthoringSnapshot(
        CadPoint3D firstCorner,
        double localXExtent,
        double localYExtent,
        double rotationRadians,
        CadRectangleCornerTreatment cornerTreatment = default)
    {
        if (!IsFinite(firstCorner))
        {
            throw new ArgumentException(
                "A RECTANG first corner must contain finite WCS coordinates.",
                nameof(firstCorner));
        }
        ValidateExtent(localXExtent, nameof(localXExtent));
        ValidateExtent(localYExtent, nameof(localYExtent));
        if (!double.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians));
        }

        double length = Math.Abs(localXExtent);
        double width = Math.Abs(localYExtent);
        switch (cornerTreatment.Mode)
        {
            case CadRectangleCornerMode.Sharp:
                break;
            case CadRectangleCornerMode.Chamfer:
                if (cornerTreatment.FirstDistance > length * 0.5 ||
                    cornerTreatment.SecondDistance > width * 0.5)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(cornerTreatment),
                        "RECTANG chamfer distances cannot exceed half their corresponding extent.");
                }
                break;
            case CadRectangleCornerMode.Fillet:
                if (cornerTreatment.FilletRadius > length * 0.5 ||
                    cornerTreatment.FilletRadius > width * 0.5)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(cornerTreatment),
                        "A RECTANG fillet radius cannot exceed half either extent.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cornerTreatment));
        }

        FirstCorner = firstCorner;
        LocalXExtent = localXExtent;
        LocalYExtent = localYExtent;
        RotationRadians = NormalizeAngle(rotationRadians);
        CornerTreatment = cornerTreatment;

        Span<CadPoint3D> points = stackalloc CadPoint3D[8];
        Span<double> bulges = stackalloc double[8];
        int count = BuildVertices(points, bulges);
        if (count < 4)
        {
            throw new ArgumentException(
                "The RECTANG contour is not representable as four distinct retained WCS vertices.");
        }
        for (int index = 0; index < count; index++)
        {
            if (!IsFinite(points[index]) || !double.IsFinite(bulges[index]))
            {
                throw new ArgumentException(
                    "The RECTANG contour must contain finite renderable vertices and bulges.");
            }
        }
    }

    public CadPoint3D VertexAt(int index)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[8];
        Span<double> bulges = stackalloc double[8];
        int count = BuildVertices(points, bulges);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return points[index];
    }

    public double BulgeAt(int index)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[8];
        Span<double> bulges = stackalloc double[8];
        int count = BuildVertices(points, bulges);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return bulges[index];
    }

    /// <summary>
    /// Copies the compact analytic contour into caller-owned bounded storage.
    /// Both destinations must hold the maximum eight RECTANG vertices.
    /// </summary>
    public int CopyContour(
        Span<CadPoint3D> destinationPoints,
        Span<double> destinationBulges)
    {
        if (destinationPoints.Length < 8 || destinationBulges.Length < 8)
        {
            throw new ArgumentException(
                "RECTANG contour storage must hold eight points and bulges.");
        }
        return BuildVertices(destinationPoints, destinationBulges);
    }

    /// <summary>Materializes one exact closed LWPOLYLINE only at commit.</summary>
    public CadPolylineAuthoringSnapshot CreatePolylineSnapshot()
    {
        Span<CadPoint3D> retainedPoints = stackalloc CadPoint3D[8];
        Span<double> retainedBulges = stackalloc double[8];
        int count = BuildVertices(retainedPoints, retainedBulges);
        var points = new CadPoint3D[count];
        var bulges = new double[count];
        retainedPoints[..count].CopyTo(points);
        retainedBulges[..count].CopyTo(bulges);
        return new CadPolylineAuthoringSnapshot(points, bulges, isClosed: true);
    }

    internal static double GetCornerAreaReduction(
        CadRectangleCornerTreatment treatment) => treatment.Mode switch
        {
            CadRectangleCornerMode.Sharp => 0.0,
            CadRectangleCornerMode.Chamfer =>
                2.0 * treatment.FirstDistance * treatment.SecondDistance,
            CadRectangleCornerMode.Fillet =>
                (4.0 - Math.PI) *
                treatment.FilletRadius *
                treatment.FilletRadius,
            _ => throw new ArgumentOutOfRangeException(nameof(treatment)),
        };

    private int BuildVertices(
        Span<CadPoint3D> destinationPoints,
        Span<double> destinationBulges)
    {
        Span<double> xs = stackalloc double[8];
        Span<double> ys = stackalloc double[8];
        Span<double> rawBulges = stackalloc double[8];
        rawBulges.Clear();
        int rawCount;
        double x = LocalXExtent;
        double y = LocalYExtent;
        double sx = Math.CopySign(1.0, x);
        double sy = Math.CopySign(1.0, y);

        switch (CornerTreatment.Mode)
        {
            case CadRectangleCornerMode.Sharp:
                rawCount = 4;
                xs[0] = 0.0;
                ys[0] = 0.0;
                xs[1] = x;
                ys[1] = 0.0;
                xs[2] = x;
                ys[2] = y;
                xs[3] = 0.0;
                ys[3] = y;
                break;

            case CadRectangleCornerMode.Chamfer:
                rawCount = 8;
                double dx = sx * CornerTreatment.FirstDistance;
                double dy = sy * CornerTreatment.SecondDistance;
                SetInsetContour(xs, ys, x, y, dx, dy);
                break;

            case CadRectangleCornerMode.Fillet:
                rawCount = 8;
                double radius = CornerTreatment.FilletRadius;
                SetInsetContour(xs, ys, x, y, sx * radius, sy * radius);
                double bulge = Orientation * QuarterCircleBulge;
                rawBulges[1] = bulge;
                rawBulges[3] = bulge;
                rawBulges[5] = bulge;
                rawBulges[7] = bulge;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(CornerTreatment));
        }

        double cosine = Math.Cos(RotationRadians);
        double sine = Math.Sin(RotationRadians);
        int count = 0;
        for (int index = 0; index < rawCount; index++)
        {
            CadPoint3D point = new(
                FirstCorner.X + (xs[index] * cosine) - (ys[index] * sine),
                FirstCorner.Y + (xs[index] * sine) + (ys[index] * cosine),
                FirstCorner.Z);
            if (count > 0 && point == destinationPoints[count - 1])
            {
                destinationBulges[count - 1] = rawBulges[index];
                continue;
            }
            if (count == destinationPoints.Length ||
                count == destinationBulges.Length)
            {
                throw new ArgumentException(
                    "The RECTANG vertex destination is too small.");
            }
            destinationPoints[count] = point;
            destinationBulges[count] = rawBulges[index];
            count++;
        }
        return count;
    }

    private static void SetInsetContour(
        Span<double> xs,
        Span<double> ys,
        double x,
        double y,
        double dx,
        double dy)
    {
        xs[0] = dx;
        ys[0] = 0.0;
        xs[1] = x - dx;
        ys[1] = 0.0;
        xs[2] = x;
        ys[2] = dy;
        xs[3] = x;
        ys[3] = y - dy;
        xs[4] = x - dx;
        ys[4] = y;
        xs[5] = dx;
        ys[5] = y;
        xs[6] = 0.0;
        ys[6] = y - dy;
        xs[7] = 0.0;
        ys[7] = dy;
    }

    private static void ValidateExtent(double extent, string parameterName)
    {
        if (!double.IsFinite(extent) ||
            extent == 0.0 ||
            Math.Abs(extent) > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                extent,
                "A RECTANG extent must be finite, nonzero, and renderable as a retained float vector.");
        }
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % Math.Tau;
        return normalized < 0.0 ? normalized + Math.Tau : normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Bounded host-neutral state for one plan-view RECTANG command.</summary>
/// <remarks>
/// Prompt storage, projection, Dimensions, Area, and preview expansion are
/// bounded O(1). Final publication expands at most eight analytic vertices.
/// </remarks>
public sealed class CadRectangleAuthoringSession
{
    private CadPoint3D _firstCorner;
    private bool _hasFirstCorner;

    public double RotationRadians { get; private set; }

    public CadRectangleCornerTreatment CornerTreatment { get; private set; }

    public CadRectangleConstruction Construction { get; }

    public CadRectangleAuthoringInputKind InputKind => _hasFirstCorner
        ? CadRectangleAuthoringInputKind.OtherCorner
        : CadRectangleAuthoringInputKind.FirstCorner;

    public int AcceptedInputCount => _hasFirstCorner ? 1 : 0;

    public CadPoint3D? FirstCorner =>
        _hasFirstCorner ? _firstCorner : null;

    public CadPoint3D? CurrentPoint => FirstCorner;

    public CadPoint3D? AcquisitionBasePoint => FirstCorner;

    public CadRectangleAuthoringSession(
        double rotationRadians = 0.0,
        CadRectangleCornerTreatment cornerTreatment = default,
        CadRectangleConstruction construction = default)
    {
        if (!TrySetRotation(rotationRadians, out string? errorMessage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationRadians),
                rotationRadians,
                errorMessage);
        }
        Construction = construction;
        ValidateFixedConstruction(construction, cornerTreatment);
        CornerTreatment = cornerTreatment;
    }

    public bool TrySetRotation(
        double rotationRadians,
        out string? errorMessage)
    {
        if (!double.IsFinite(rotationRadians))
        {
            errorMessage = "A RECTANG rotation must be finite.";
            return false;
        }
        double normalized = rotationRadians % Math.Tau;
        RotationRadians = normalized < 0.0
            ? normalized + Math.Tau
            : normalized;
        errorMessage = null;
        return true;
    }

    public bool TrySetChamfer(
        double firstDistance,
        double secondDistance,
        out string? errorMessage)
    {
        try
        {
            CadRectangleCornerTreatment treatment =
                CadRectangleCornerTreatment.Chamfer(
                firstDistance,
                secondDistance);
            ValidateFixedConstruction(Construction, treatment);
            CornerTreatment = treatment;
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    public bool TrySetFillet(double radius, out string? errorMessage)
    {
        try
        {
            CadRectangleCornerTreatment treatment =
                CadRectangleCornerTreatment.Fillet(radius);
            ValidateFixedConstruction(Construction, treatment);
            CornerTreatment = treatment;
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    public void SetSharpCorners() =>
        CornerTreatment = CadRectangleCornerTreatment.Sharp;

    public bool CanAcceptPoint(CadPoint3D point) =>
        TryProcessPoint(
            point,
            acceptFirst: false,
            out _,
            out _,
            out _);

    public bool TryPreviewPoint(
        CadPoint3D point,
        out CadRectangleAuthoringSnapshot snapshot)
    {
        bool accepted = TryProcessPoint(
            point,
            acceptFirst: false,
            out snapshot,
            out bool completed,
            out _);
        return accepted && completed;
    }

    /// <summary>
    /// Accepts a corner. The final solve does not mutate prompt state so a
    /// publication preflight failure can be corrected in place.
    /// </summary>
    public bool TryAcceptPoint(
        CadPoint3D point,
        out CadRectangleAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage) =>
        TryProcessPoint(
            point,
            acceptFirst: true,
            out snapshot,
            out completed,
            out errorMessage);

    public bool TryCreateFromDimensions(
        double length,
        double width,
        CadPoint3D placementPoint,
        out CadRectangleAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (!_hasFirstCorner)
        {
            errorMessage =
                "Accept the first RECTANG corner before entering Dimensions.";
            return false;
        }
        if (!IsPositiveRenderable(length) || !IsPositiveRenderable(width))
        {
            errorMessage =
                "RECTANG length and width must be finite, positive, and renderable.";
            return false;
        }
        if (!TryGetPlacementSigns(
                placementPoint,
                out double xSign,
                out double ySign,
                out errorMessage))
        {
            return false;
        }
        return TryCreateSnapshot(
            xSign * length,
            ySign * width,
            out snapshot,
            out errorMessage);
    }

    public bool TryCreateFromArea(
        double area,
        CadRectangleKnownDimension knownDimension,
        double knownValue,
        CadPoint3D placementPoint,
        out CadRectangleAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (!Enum.IsDefined(knownDimension))
        {
            errorMessage = "The RECTANG Area dimension selector is invalid.";
            return false;
        }
        if (!double.IsFinite(area) || area <= 0.0)
        {
            errorMessage = "A RECTANG Area value must be finite and positive.";
            return false;
        }
        if (!IsPositiveRenderable(knownValue))
        {
            errorMessage =
                "The known RECTANG dimension must be finite, positive, and renderable.";
            return false;
        }

        double reduction =
            CadRectangleAuthoringSnapshot.GetCornerAreaReduction(CornerTreatment);
        double outerArea = area + reduction;
        double missingValue = outerArea / knownValue;
        if (!double.IsFinite(outerArea) ||
            !IsPositiveRenderable(missingValue))
        {
            errorMessage =
                "The requested RECTANG Area does not produce finite renderable dimensions.";
            return false;
        }

        double length = knownDimension == CadRectangleKnownDimension.Length
            ? knownValue
            : missingValue;
        double width = knownDimension == CadRectangleKnownDimension.Width
            ? knownValue
            : missingValue;
        return TryCreateFromDimensions(
            length,
            width,
            placementPoint,
            out snapshot,
            out errorMessage);
    }

    private bool TryProcessPoint(
        CadPoint3D point,
        bool acceptFirst,
        out CadRectangleAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!IsFinite(point))
        {
            errorMessage =
                "A RECTANG corner must contain finite WCS coordinates.";
            return false;
        }
        if (!_hasFirstCorner)
        {
            if (acceptFirst)
            {
                _firstCorner = point;
                _hasFirstCorner = true;
            }
            errorMessage = null;
            return true;
        }
        if (point.Z != _firstCorner.Z)
        {
            errorMessage =
                "A plan-view RECTANG corner must remain on the first corner's WCS-Z plane.";
            return false;
        }

        bool solved;
        switch (Construction.Mode)
        {
            case CadRectangleConstructionMode.DiagonalCorners:
                double dx = point.X - _firstCorner.X;
                double dy = point.Y - _firstCorner.Y;
                double cosine = Math.Cos(RotationRadians);
                double sine = Math.Sin(RotationRadians);
                double localX = (dx * cosine) + (dy * sine);
                double localY = (-dx * sine) + (dy * cosine);
                solved = TryCreateSnapshot(
                    localX,
                    localY,
                    out snapshot,
                    out errorMessage);
                break;
            case CadRectangleConstructionMode.Dimensions:
                solved = TryCreateFromDimensions(
                    Construction.Length,
                    Construction.Width,
                    point,
                    out snapshot,
                    out errorMessage);
                break;
            case CadRectangleConstructionMode.Area:
                solved = TryCreateFromArea(
                    Construction.Area,
                    Construction.KnownDimension,
                    Construction.KnownValue,
                    point,
                    out snapshot,
                    out errorMessage);
                break;
            default:
                errorMessage = "The RECTANG construction mode is invalid.";
                solved = false;
                break;
        }
        if (!solved)
        {
            return false;
        }

        completed = true;
        return true;
    }

    private bool TryGetPlacementSigns(
        CadPoint3D placementPoint,
        out double xSign,
        out double ySign,
        out string? errorMessage)
    {
        xSign = 0.0;
        ySign = 0.0;
        if (!IsFinite(placementPoint))
        {
            errorMessage =
                "A RECTANG placement point must contain finite WCS coordinates.";
            return false;
        }
        if (placementPoint.Z != _firstCorner.Z)
        {
            errorMessage =
                "A plan-view RECTANG placement point must remain on the first corner's WCS-Z plane.";
            return false;
        }

        double dx = placementPoint.X - _firstCorner.X;
        double dy = placementPoint.Y - _firstCorner.Y;
        double cosine = Math.Cos(RotationRadians);
        double sine = Math.Sin(RotationRadians);
        double localX = (dx * cosine) + (dy * sine);
        double localY = (-dx * sine) + (dy * cosine);
        if (!double.IsFinite(localX) || !double.IsFinite(localY))
        {
            errorMessage =
                "The RECTANG placement point does not define finite local directions.";
            return false;
        }
        xSign = localX < 0.0 ? -1.0 : 1.0;
        ySign = localY < 0.0 ? -1.0 : 1.0;
        errorMessage = null;
        return true;
    }

    private bool TryCreateSnapshot(
        double localX,
        double localY,
        out CadRectangleAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        try
        {
            snapshot = new CadRectangleAuthoringSnapshot(
                _firstCorner,
                localX,
                localY,
                RotationRadians,
                CornerTreatment);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool IsPositiveRenderable(double value) =>
        double.IsFinite(value) &&
        value > 0.0 &&
        value <= float.MaxValue;

    private static void ValidateFixedConstruction(
        CadRectangleConstruction construction,
        CadRectangleCornerTreatment treatment)
    {
        switch (construction.Mode)
        {
            case CadRectangleConstructionMode.DiagonalCorners:
                return;
            case CadRectangleConstructionMode.Dimensions:
                _ = new CadRectangleAuthoringSnapshot(
                    CadPoint3D.Zero,
                    construction.Length,
                    construction.Width,
                    0.0,
                    treatment);
                return;
            case CadRectangleConstructionMode.Area:
                double outerArea = construction.Area +
                    CadRectangleAuthoringSnapshot.GetCornerAreaReduction(
                        treatment);
                double missing = outerArea / construction.KnownValue;
                double length = construction.KnownDimension ==
                        CadRectangleKnownDimension.Length
                    ? construction.KnownValue
                    : missing;
                double width = construction.KnownDimension ==
                        CadRectangleKnownDimension.Width
                    ? construction.KnownValue
                    : missing;
                _ = new CadRectangleAuthoringSnapshot(
                    CadPoint3D.Zero,
                    length,
                    width,
                    0.0,
                    treatment);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(construction));
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
