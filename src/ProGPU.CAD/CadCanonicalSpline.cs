namespace ProGPU.CAD;

/// <summary>
/// Provides one validated standard B-spline view over ordinary, compact
/// periodic, and already cyclically extended CAD spline records.
/// </summary>
/// <remarks>
/// Autodesk's public periodic SPLINE contract stores N control points and
/// N+1 knots. A standard evaluator instead consumes the first P controls a
/// second time and P cyclic knot intervals on either side for degree P. This
/// view computes that expansion in O(1) per indexed value without modifying
/// the immutable document snapshot. Closed nonperiodic records retain their
/// ordinary knot/control representation and expose a separate closing-edge
/// flag to callers that own path topology.
/// </remarks>
internal readonly struct CadCanonicalSpline
{
    private readonly CadDocumentSnapshot _snapshot;
    private readonly CadSplinePrimitive _spline;

    internal CadCanonicalSpline(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline)
    {
        _snapshot = snapshot;
        _spline = spline;
    }

    public int Degree => _spline.Degree;

    public int ControlPointCount => _spline.IsPeriodic
        ? checked(_spline.ControlPointCount + _spline.Degree)
        : _spline.ControlPointCount;

    public int KnotCount => _spline.IsPeriodic
        ? checked(_spline.ControlPointCount + (2 * _spline.Degree) + 1)
        : _spline.KnotCount;

    public bool IsLoop => _spline.IsClosed || _spline.IsPeriodic;

    public bool HasClosingEdge => _spline.IsClosed && !_spline.IsPeriodic;

    public bool HasWeights => _spline.WeightCount != 0;

    public CadPoint3D GetControlPoint(int index)
    {
        int sourceIndex = index < _spline.ControlPointCount
            ? index
            : index - _spline.ControlPointCount;
        return _snapshot.SplineControlPoints.Span[_spline.ControlPointOffset + sourceIndex];
    }

    public double GetWeight(int index)
    {
        if (_spline.WeightCount == 0)
        {
            return 1.0;
        }

        int sourceIndex = index < _spline.ControlPointCount
            ? index
            : index - _spline.ControlPointCount;
        return _snapshot.SplineWeights.Span[_spline.WeightOffset + sourceIndex];
    }

    public double GetKnot(int index)
    {
        ReadOnlySpan<double> source = _snapshot.SplineKnots.Span.Slice(
            _spline.KnotOffset,
            _spline.KnotCount);
        if (!_spline.IsPeriodic)
        {
            return source[index];
        }

        if (_spline.KnotCount == KnotCount)
        {
            return source[index];
        }

        int degree = _spline.Degree;
        int sourceCount = _spline.ControlPointCount;
        double period = source[^1] - source[0];
        if (index < degree)
        {
            return source[sourceCount - degree + index] - period;
        }

        int sourceIndex = index - degree;
        if (sourceIndex <= sourceCount)
        {
            return source[sourceIndex];
        }

        return source[sourceIndex - sourceCount] + period;
    }
}

internal static class CadSplineCanonicalizer
{
    public static bool TryCreate(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        out CadCanonicalSpline canonical)
    {
        canonical = default;
        int degree = spline.Degree;
        int controlPointCount = spline.ControlPointCount;
        if (degree < 1 || degree > 10 || controlPointCount < degree + 1 ||
            controlPointCount > int.MaxValue - (2 * degree) - 1 ||
            (spline.WeightCount != 0 && spline.WeightCount != controlPointCount))
        {
            return false;
        }

        int ordinaryKnotCount = checked(controlPointCount + degree + 1);
        int compactPeriodicKnotCount = checked(controlPointCount + 1);
        int expandedPeriodicKnotCount = checked(controlPointCount + (2 * degree) + 1);
        bool hasSupportedKnotCount = spline.IsPeriodic
            ? spline.KnotCount == compactPeriodicKnotCount ||
                spline.KnotCount == expandedPeriodicKnotCount
            : spline.KnotCount == ordinaryKnotCount;
        if (!hasSupportedKnotCount)
        {
            return false;
        }

        ReadOnlySpan<double> knots = snapshot.SplineKnots.Span.Slice(
            spline.KnotOffset,
            spline.KnotCount);
        for (int i = 0; i < knots.Length; i++)
        {
            if (!double.IsFinite(knots[i]) || (i != 0 && knots[i] < knots[i - 1]))
            {
                return false;
            }
        }

        if (spline.IsPeriodic && !(knots[^1] > knots[0]))
        {
            return false;
        }

        if (spline.IsPeriodic && spline.KnotCount == expandedPeriodicKnotCount)
        {
            double period = knots[degree + controlPointCount] - knots[degree];
            if (!(period > 0.0))
            {
                return false;
            }

            for (int i = 0; i < degree; i++)
            {
                double expectedLeft = knots[controlPointCount + i] - period;
                double expectedRight = knots[degree + i + 1] + period;
                if (!NearlyEqual(knots[i], expectedLeft) ||
                    !NearlyEqual(knots[degree + controlPointCount + i + 1], expectedRight))
                {
                    return false;
                }
            }
        }

        if (spline.WeightCount != 0)
        {
            ReadOnlySpan<double> weights = snapshot.SplineWeights.Span.Slice(
                spline.WeightOffset,
                spline.WeightCount);
            for (int i = 0; i < weights.Length; i++)
            {
                if (!double.IsFinite(weights[i]) || weights[i] <= 0.0)
                {
                    return false;
                }
            }
        }

        canonical = new CadCanonicalSpline(snapshot, spline);
        int domainEndIndex = canonical.ControlPointCount;
        if (!(canonical.GetKnot(domainEndIndex) > canonical.GetKnot(degree)))
        {
            canonical = default;
            return false;
        }

        return true;
    }

    private static bool NearlyEqual(double left, double right)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
