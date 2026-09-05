using CSMath;
using HatchSpline = ACadSharp.Entities.Hatch.BoundaryPath.Spline;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private readonly struct CadHatchCanonicalSpline : ICadCanonicalSpline
    {
        private readonly HatchSpline _source;

        public CadHatchCanonicalSpline(HatchSpline source)
        {
            _source = source;
        }

        public int Degree => _source.Degree;

        public int ControlPointCount => _source.IsPeriodic
            ? checked(_source.ControlPoints.Count + _source.Degree)
            : _source.ControlPoints.Count;

        public bool IsPeriodic => _source.IsPeriodic;

        public CadPoint3D GetControlPoint(int index)
        {
            int sourceCount = _source.ControlPoints.Count;
            XYZ value = _source.ControlPoints[index < sourceCount ? index : index - sourceCount];
            return new CadPoint3D(value.X, value.Y, 0.0);
        }

        public double GetWeight(int index)
        {
            if (!_source.IsRational)
            {
                return 1.0;
            }
            int sourceCount = _source.ControlPoints.Count;
            return _source.ControlPoints[index < sourceCount ? index : index - sourceCount].Z;
        }

        public double GetKnot(int index)
        {
            List<double> source = _source.Knots;
            if (!_source.IsPeriodic || source.Count == CanonicalKnotCount)
            {
                return source[index];
            }

            int degree = _source.Degree;
            int sourceCount = _source.ControlPoints.Count;
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

        private int CanonicalKnotCount =>
            checked(_source.ControlPoints.Count + (2 * _source.Degree) + 1);
    }

    private static void AddSplineEdge(
        HatchSpline spline,
        List<CadHatchSegment> destination,
        CadHatchSplineSourceBudget sourceBudget,
        ref bool hasCurves)
    {
        int scalarValueCount = checked(
            checked(spline.ControlPoints.Count * 3) +
            spline.Knots.Count +
            checked(spline.FitPoints.Count * 2) +
            (spline.FitPoints.Count == 0 ? 0 : 4));
        sourceBudget.Consume(scalarValueCount);

        ValidateHatchSplineSource(spline);
        var canonical = new CadHatchCanonicalSpline(spline);
        Span<CadHomogeneousPoint> controls = stackalloc CadHomogeneousPoint[4];
        int emitted = 0;
        for (int sourceSpan = canonical.Degree;
             sourceSpan < canonical.ControlPointCount;
             sourceSpan++)
        {
            if (!(canonical.GetKnot(sourceSpan + 1) > canonical.GetKnot(sourceSpan)))
            {
                continue;
            }

            Span<CadHomogeneousPoint> span = controls[..(canonical.Degree + 1)];
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span))
            {
                throw new ArgumentException(
                    "A HATCH spline edge has invalid knot multiplicity or cannot isolate an exact Bezier span.");
            }
            AddBezierSpan(span, canonical.Degree, destination);
            emitted++;
        }

        if (emitted == 0)
        {
            throw new ArgumentException("A HATCH spline edge has no non-empty knot span.");
        }
        hasCurves |= canonical.Degree > 1;
    }

    private static void ValidateHatchSplineSource(HatchSpline spline)
    {
        if (spline.Degree < 1)
        {
            throw new ArgumentException("A HATCH spline edge requires a positive degree.");
        }
        if (spline.Degree > 3)
        {
            throw new CadUnsupportedEntityException(
                $"Degree-{spline.Degree} HATCH spline edges require a shared filled-path segment above cubic degree.");
        }
        int controlCount = spline.ControlPoints.Count;
        if (controlCount < spline.Degree + 1)
        {
            throw new ArgumentException(
                "A HATCH spline edge has fewer control points than its degree requires.");
        }

        int ordinaryKnotCount = checked(controlCount + spline.Degree + 1);
        int compactPeriodicKnotCount = checked(controlCount + 1);
        int expandedPeriodicKnotCount = checked(controlCount + (2 * spline.Degree) + 1);
        bool validKnotCount = spline.IsPeriodic
            ? spline.Knots.Count == compactPeriodicKnotCount ||
                spline.Knots.Count == expandedPeriodicKnotCount
            : spline.Knots.Count == ordinaryKnotCount;
        if (!validKnotCount)
        {
            throw new ArgumentException(
                "A HATCH spline edge knot count does not match its degree, controls, and periodic flag.");
        }

        for (int i = 0; i < controlCount; i++)
        {
            XYZ point = spline.ControlPoints[i];
            EnsureFiniteHatchPoint(point.X, point.Y);
            if (!spline.IsRational)
            {
                continue;
            }
            if (!double.IsFinite(point.Z) || point.Z <= 0.0)
            {
                throw new ArgumentException(
                    "Rational HATCH spline weights must be finite and positive.");
            }
        }

        for (int i = 0; i < spline.Knots.Count; i++)
        {
            double knot = spline.Knots[i];
            if (!double.IsFinite(knot) ||
                (i != 0 && knot < spline.Knots[i - 1]))
            {
                throw new ArgumentException(
                    "HATCH spline knots must be finite and nondecreasing.");
            }
        }

        var canonical = new CadHatchCanonicalSpline(spline);
        if (!(canonical.GetKnot(canonical.ControlPointCount) >
              canonical.GetKnot(canonical.Degree)))
        {
            throw new ArgumentException("A HATCH spline edge has an empty parameter domain.");
        }

        if (spline.IsPeriodic && spline.Knots.Count == expandedPeriodicKnotCount)
        {
            double period = spline.Knots[spline.Degree + controlCount] -
                spline.Knots[spline.Degree];
            if (!(period > 0.0))
            {
                throw new ArgumentException("A periodic HATCH spline requires a positive knot period.");
            }
            for (int i = 0; i < spline.Degree; i++)
            {
                double expectedLeft = spline.Knots[controlCount + i] - period;
                double expectedRight = spline.Knots[spline.Degree + i + 1] + period;
                if (!NearlyEqualHatchSplineKnot(spline.Knots[i], expectedLeft) ||
                    !NearlyEqualHatchSplineKnot(
                        spline.Knots[spline.Degree + controlCount + i + 1],
                        expectedRight))
                {
                    throw new ArgumentException(
                        "An expanded periodic HATCH spline knot vector is not cyclically consistent.");
                }
            }
        }

        for (int i = 0; i < spline.FitPoints.Count; i++)
        {
            EnsureFiniteHatchPoint(spline.FitPoints[i].X, spline.FitPoints[i].Y);
        }
        if (spline.FitPoints.Count != 0)
        {
            EnsureFiniteHatchPoint(spline.StartTangent.X, spline.StartTangent.Y);
            EnsureFiniteHatchPoint(spline.EndTangent.X, spline.EndTangent.Y);
        }
    }

    private static void AddBezierSpan(
        ReadOnlySpan<CadHomogeneousPoint> controls,
        int degree,
        List<CadHatchSegment> destination)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[4];
        for (int i = 0; i < controls.Length; i++)
        {
            points[i] = controls[i].Cartesian;
            EnsureFiniteHatchPoint(points[i].X, points[i].Y);
        }

        switch (degree)
        {
            case 1:
                AddLineSegment(
                    points[0].X,
                    points[0].Y,
                    points[1].X,
                    points[1].Y,
                    destination);
                return;
            case 2:
                double canonicalWeight = GetCanonicalQuadraticWeight(controls);
                double coordinateScale = 1.0;
                for (int i = 0; i < 3; i++)
                {
                    coordinateScale = Math.Max(
                        coordinateScale,
                        Math.Max(Math.Abs(points[i].X), Math.Abs(points[i].Y)));
                }
                if (canonicalWeight >
                    float.MaxValue / (4.0 * coordinateScale))
                {
                    throw new CadUnsupportedEntityException(
                        "A rational quadratic HATCH span exceeds the shared-path weighted-coordinate range.");
                }
                destination.Add(new CadHatchSegment(
                    NearlyEqualHatchSplineWeight(canonicalWeight, 1.0)
                        ? CadHatchSegmentKind.QuadraticBezier
                        : CadHatchSegmentKind.RationalQuadraticBezier,
                    points[0].X,
                    points[0].Y,
                    points[2].X,
                    points[2].Y,
                    points[1].X,
                    points[1].Y,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    canonicalWeight));
                return;
            case 3:
                (double canonicalWeight1, double canonicalWeight2) =
                    GetCanonicalCubicWeights(controls);
                double cubicCoordinateScale = 1.0;
                for (int i = 0; i < 4; i++)
                {
                    cubicCoordinateScale = Math.Max(
                        cubicCoordinateScale,
                        Math.Max(Math.Abs(points[i].X), Math.Abs(points[i].Y)));
                }
                if (Math.Max(canonicalWeight1, canonicalWeight2) >
                    float.MaxValue / (8.0 * cubicCoordinateScale))
                {
                    throw new CadUnsupportedEntityException(
                        "A rational cubic HATCH span exceeds the shared-path weighted-coordinate range.");
                }
                destination.Add(new CadHatchSegment(
                    NearlyEqualHatchSplineWeight(canonicalWeight1, 1.0) &&
                    NearlyEqualHatchSplineWeight(canonicalWeight2, 1.0)
                        ? CadHatchSegmentKind.CubicBezier
                        : CadHatchSegmentKind.RationalCubicBezier,
                    points[0].X,
                    points[0].Y,
                    points[3].X,
                    points[3].Y,
                    points[1].X,
                    points[1].Y,
                    points[2].X,
                    points[2].Y,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    canonicalWeight1,
                    canonicalWeight2));
                return;
            default:
                throw new InvalidOperationException("Only Bezier degrees one through three are retained.");
        }
    }

    private static double GetCanonicalQuadraticWeight(
        ReadOnlySpan<CadHomogeneousPoint> controls)
    {
        if (!CadRationalBezier.TryGetCanonicalQuadraticWeight(
                controls,
                out double weight))
        {
            throw new CadUnsupportedEntityException(
                "A rational quadratic HATCH span cannot be represented by a finite positive shared-path weight.");
        }
        return weight;
    }

    private static (double Weight1, double Weight2) GetCanonicalCubicWeights(
        ReadOnlySpan<CadHomogeneousPoint> controls)
    {
        if (!CadRationalBezier.TryGetCanonicalCubicWeights(
                controls,
                out double weight1,
                out double weight2))
        {
            throw new CadUnsupportedEntityException(
                "A rational cubic HATCH span cannot be represented by finite positive shared-path weights.");
        }
        return (weight1, weight2);
    }

    private static bool NearlyEqualHatchSplineWeight(double left, double right)
    {
        double scale = Math.Max(Math.Abs(left), Math.Abs(right));
        return Math.Abs(left - right) <= scale * 1e-13;
    }

    private static bool NearlyEqualHatchSplineKnot(double left, double right)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
