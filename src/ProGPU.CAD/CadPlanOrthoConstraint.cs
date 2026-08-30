namespace ProGPU.CAD;

public enum CadPlanOrthoAxis : byte
{
    X = 0,
    Y = 1,
}

public readonly record struct CadPlanOrthoResult(
    CadPoint3D Point,
    CadPlanOrthoAxis Axis,
    bool IsGridSnapped);

/// <summary>Exact base-relative orthogonal point constraint.</summary>
/// <remarks>
/// A query chooses the nearest of the active rectangular snap basis axes,
/// preserving an exact one-axis displacement from the accepted base. When grid
/// snap is enabled, only the moving coordinate is taken from the lattice so an
/// off-grid object-snap base remains exactly orthogonal. Work is O(1) and
/// allocation-free.
/// </remarks>
public static class CadPlanOrthoConstraint
{
    public static bool TryConstrain(
        CadPoint3D basePoint,
        CadPoint3D pointerPoint,
        CadPlanGridSnapSettings basis,
        out CadPlanOrthoResult result)
    {
        result = default;
        if (!basis.IsSupported ||
            !IsFinite(basePoint) ||
            !IsFinite(pointerPoint))
        {
            return false;
        }

        CadPoint3D delta = pointerPoint - basePoint;
        double x = CadPoint3D.Dot(delta, basis.XAxis);
        double y = CadPoint3D.Dot(delta, basis.YAxis);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        CadPlanOrthoAxis axis = Math.Abs(y) <= Math.Abs(x)
            ? CadPlanOrthoAxis.X
            : CadPlanOrthoAxis.Y;
        CadPoint3D direction = axis == CadPlanOrthoAxis.X
            ? basis.XAxis
            : basis.YAxis;
        double distance = axis == CadPlanOrthoAxis.X ? x : y;
        bool isGridSnapped = false;
        if (basis.IsEnabled)
        {
            if (!basis.TrySnap(pointerPoint, out CadPoint3D gridPoint))
            {
                return false;
            }

            distance = CadPoint3D.Dot(gridPoint - basePoint, direction);
            if (!double.IsFinite(distance))
            {
                return false;
            }
            isGridSnapped = true;
        }

        CadPoint3D constrained = basePoint + (direction * distance);
        if (!IsFinite(constrained))
        {
            return false;
        }

        result = new CadPlanOrthoResult(constrained, axis, isGridSnapped);
        return true;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
