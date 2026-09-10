using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Immutable WCS input for one POINT command.</summary>
public readonly record struct CadPointAuthoringSnapshot
{
    public CadPoint3D Location { get; }

    public CadPointAuthoringSnapshot(CadPoint3D location)
    {
        if (!IsFinite(location))
        {
            throw new ArgumentException(
                "A POINT location must contain finite WCS coordinates.",
                nameof(location));
        }

        Location = location;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Bounded host-neutral acquisition state for one POINT command.</summary>
/// <remarks>
/// POINT accepts exactly one WCS location and completes immediately. Validation
/// is O(1), the session retains no growing collection, and MULTIPLE remains a
/// separate command-repetition concern.
/// </remarks>
public sealed class CadPointAuthoringSession
{
    public bool TryCreateSnapshot(
        CadPoint3D location,
        out CadPointAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        try
        {
            snapshot = new CadPointAuthoringSnapshot(location);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            snapshot = default;
            errorMessage = exception.Message;
            return false;
        }
    }
}

/// <summary>Adds one POINT entity as one reversible history operation.</summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, THICKNESS, and the
/// active UCS marker orientation are captured atomically on first Apply.
/// Unsupported nonzero THICKNESS fails before mutation. Apply/Undo/Redo are O(1).
/// </remarks>
public sealed class CadAddPointCommand : CadEditCommand
{
    private readonly CadPointAuthoringSnapshot _snapshot;
    private ACadSharp.Entities.Point? _point;

    public CadPointAuthoringSnapshot Snapshot => _snapshot;

    public ACadSharp.Entities.Point? Point => _point;

    public ulong CurrentHandle => _point?.Handle ?? 0;

    public CadAddPointCommand(
        CadPointAuthoringSnapshot snapshot,
        string description = "POINT")
        : base(description)
    {
        _snapshot = snapshot;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        ACadSharp.Entities.Point point;
        if (isRedo)
        {
            point = _point ?? throw new InvalidOperationException(
                "The POINT command has not been applied.");
        }
        else
        {
            point = CreatePoint(document);
            _point = point;
        }

        ValidateDetached(point);
        document.Entities.Add(point);
    }

    internal override void Revert(CadDocument document)
    {
        ACadSharp.Entities.Point point = _point ??
            throw new InvalidOperationException(
                "The POINT command has not been applied.");
        ValidateModelSpaceEntity(document, point);
        if (!document.Entities.Remove(point))
        {
            throw new InvalidOperationException(
                "The authored POINT could not be removed from model space.");
        }
    }

    private ACadSharp.Entities.Point CreatePoint(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive a POINT entity.");
        }

        double thickness = document.Header.ThicknessDefault;
        if (!double.IsFinite(thickness))
        {
            throw new InvalidOperationException(
                "Current THICKNESS must be finite before creating a POINT.");
        }
        if (thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Nonzero THICKNESS requires retained point-extrusion geometry and is not authored as a planar marker.");
        }

        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating a POINT.");
        }

        ValidatePointDisplay(
            document.Header.PointDisplayMode,
            document.Header.PointDisplaySize);
        CaptureMarkerOrientation(
            document,
            document.Header.PointDisplayMode,
            out XYZ normal,
            out double rotation);

        return new ACadSharp.Entities.Point(new XYZ(
            _snapshot.Location.X,
            _snapshot.Location.Y,
            _snapshot.Location.Z))
        {
            Normal = normal,
            Rotation = rotation,
            Thickness = thickness,
            Layer = layer,
            Color = document.Header.CurrentEntityColor,
            LineType = document.Header.CurrentLineType,
            LineTypeScale = lineTypeScale,
            LineWeight = document.Header.CurrentEntityLineWeight,
        };
    }

    private static void CaptureMarkerOrientation(
        CadDocument document,
        short displayMode,
        out XYZ normal,
        out double rotation)
    {
        normal = XYZ.AxisZ;
        rotation = 0.0;
        if (displayMode == 0)
        {
            return;
        }

        if (!document.VPorts.TryGetValue(VPort.DefaultName, out VPort? active) ||
            active is null)
        {
            throw new InvalidOperationException(
                "The active viewport is required to capture POINT marker orientation.");
        }

        var xAxis = new CadPoint3D(
            active.XAxis.X,
            active.XAxis.Y,
            active.XAxis.Z);
        var yAxis = new CadPoint3D(
            active.YAxis.X,
            active.YAxis.Y,
            active.YAxis.Z);
        try
        {
            xAxis = xAxis.Normalize();
            yAxis = yAxis.Normalize();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The active UCS axes must be finite and nonzero before creating a POINT.",
                exception);
        }

        double axesDot = CadPoint3D.Dot(xAxis, yAxis);
        if (!double.IsFinite(axesDot) || Math.Abs(axesDot) > 1e-10)
        {
            throw new InvalidOperationException(
                "The active UCS axes must be orthogonal before creating a POINT.");
        }

        CadPoint3D zAxis;
        try
        {
            zAxis = CadPoint3D.Cross(xAxis, yAxis).Normalize();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The active UCS axes must define a finite POINT marker plane.",
                exception);
        }

        CadCoordinateSystem localBasis = CadCoordinateSystem.FromNormal(zAxis);
        double x = CadPoint3D.Dot(xAxis, localBasis.XAxis);
        double y = CadPoint3D.Dot(xAxis, localBasis.YAxis);
        rotation = Math.Atan2(y, x);
        if (!double.IsFinite(rotation))
        {
            throw new InvalidOperationException(
                "The active UCS rotation must be finite before creating a POINT.");
        }

        normal = new XYZ(zAxis.X, zAxis.Y, zAxis.Z);
    }

    private static void ValidatePointDisplay(short displayMode, double displaySize)
    {
        int baseMode = displayMode & 31;
        int enclosureMode = displayMode & 96;
        if (displayMode < 0 || baseMode > 4 ||
            enclosureMode is not (0 or 32 or 64 or 96) ||
            displayMode != baseMode + enclosureMode)
        {
            throw new CadUnsupportedEntityException(
                $"PDMODE {displayMode} is outside the documented base and enclosure combinations.");
        }
        if (!double.IsFinite(displaySize))
        {
            throw new InvalidOperationException(
                "Current PDSIZE must be finite before creating a POINT.");
        }
    }

    private static void ValidateDetached(ACadSharp.Entities.Point point)
    {
        if (point.Owner is not null ||
            point.Document is not null ||
            point.Handle != 0)
        {
            throw new InvalidOperationException(
                "The retained POINT entity is not detached and cannot be added.");
        }
    }
}
