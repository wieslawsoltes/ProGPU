namespace ProGPU.CAD;

/// <summary>A double-precision point or vector in CAD world coordinates.</summary>
public readonly record struct CadPoint3D(double X, double Y, double Z)
{
    public static CadPoint3D Zero => default;

    public double Length
    {
        get
        {
            double maximum = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            if (maximum == 0.0)
            {
                return 0.0;
            }

            double x = X / maximum;
            double y = Y / maximum;
            double z = Z / maximum;
            return maximum * Math.Sqrt((x * x) + (y * y) + (z * z));
        }
    }

    public CadPoint3D Normalize()
    {
        double maximum = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        if (!double.IsFinite(maximum) || maximum <= 0.0)
        {
            throw new ArgumentException("A CAD direction must be finite and non-zero.");
        }

        CadPoint3D scaled = this / maximum;
        double scaledLength = Math.Sqrt(Dot(scaled, scaled));
        return scaled / scaledLength;
    }

    public static double Dot(CadPoint3D left, CadPoint3D right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    public static CadPoint3D Cross(CadPoint3D left, CadPoint3D right) =>
        new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));

    public static CadPoint3D operator +(CadPoint3D left, CadPoint3D right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static CadPoint3D operator -(CadPoint3D left, CadPoint3D right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static CadPoint3D operator *(CadPoint3D value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    public static CadPoint3D operator /(CadPoint3D value, double scale) =>
        new(value.X / scale, value.Y / scale, value.Z / scale);
}

/// <summary>
/// An orthonormal object-coordinate-system basis expressed in WCS.
/// </summary>
/// <remarks>
/// This is an original ProGPU implementation of Autodesk's documented arbitrary-axis
/// algorithm. It intentionally remains independent of ACadSharp matrix internals.
/// </remarks>
public readonly record struct CadCoordinateSystem(
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    CadPoint3D ZAxis)
{
    private const double AxisThreshold = 1.0 / 64.0;

    public static CadCoordinateSystem FromNormal(CadPoint3D normal)
    {
        CadPoint3D zAxis = normal.Normalize();
        CadPoint3D xAxis = Math.Abs(zAxis.X) < AxisThreshold && Math.Abs(zAxis.Y) < AxisThreshold
            ? CadPoint3D.Cross(new CadPoint3D(0.0, 1.0, 0.0), zAxis).Normalize()
            : CadPoint3D.Cross(new CadPoint3D(0.0, 0.0, 1.0), zAxis).Normalize();
        CadPoint3D yAxis = CadPoint3D.Cross(zAxis, xAxis).Normalize();
        return new CadCoordinateSystem(xAxis, yAxis, zAxis);
    }

    public CadPoint3D Transform(CadPoint3D objectPoint) =>
        (XAxis * objectPoint.X) + (YAxis * objectPoint.Y) + (ZAxis * objectPoint.Z);

    public CadPoint3D PointOnCircle(CadPoint3D center, double radius, double angle) =>
        center + (XAxis * (radius * Math.Cos(angle))) + (YAxis * (radius * Math.Sin(angle)));
}

/// <summary>An immutable axis-aligned world-space bounding box.</summary>
public readonly struct CadBounds3D : IEquatable<CadBounds3D>
{
    private readonly bool _hasValue;

    public static CadBounds3D Empty => default;

    public CadPoint3D Min { get; }
    public CadPoint3D Max { get; }
    public bool IsEmpty => !_hasValue;

    public CadPoint3D Center => IsEmpty
        ? CadPoint3D.Zero
        : new CadPoint3D(
            (Min.X * 0.5) + (Max.X * 0.5),
            (Min.Y * 0.5) + (Max.Y * 0.5),
            (Min.Z * 0.5) + (Max.Z * 0.5));

    public CadBounds3D(CadPoint3D min, CadPoint3D max)
    {
        if (!AreFinite(min) || !AreFinite(max) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            throw new ArgumentException("CAD bounds must be finite and ordered.");
        }

        Min = min;
        Max = max;
        _hasValue = true;
    }

    public static CadBounds3D FromPoint(CadPoint3D point) => new(point, point);

    public CadBounds3D Include(CadPoint3D point)
    {
        if (!AreFinite(point))
        {
            throw new ArgumentException("A CAD bound point must be finite.", nameof(point));
        }

        return IsEmpty
            ? FromPoint(point)
            : new CadBounds3D(
                new CadPoint3D(
                    Math.Min(Min.X, point.X),
                    Math.Min(Min.Y, point.Y),
                    Math.Min(Min.Z, point.Z)),
                new CadPoint3D(
                    Math.Max(Max.X, point.X),
                    Math.Max(Max.Y, point.Y),
                    Math.Max(Max.Z, point.Z)));
    }

    public CadBounds3D Union(CadBounds3D other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return new CadBounds3D(
            new CadPoint3D(
                Math.Min(Min.X, other.Min.X),
                Math.Min(Min.Y, other.Min.Y),
                Math.Min(Min.Z, other.Min.Z)),
            new CadPoint3D(
                Math.Max(Max.X, other.Max.X),
                Math.Max(Max.Y, other.Max.Y),
                Math.Max(Max.Z, other.Max.Z)));
    }

    public bool Intersects(CadBounds3D other) =>
        !IsEmpty && !other.IsEmpty &&
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Equals(CadBounds3D other) =>
        IsEmpty == other.IsEmpty && (IsEmpty || (Min == other.Min && Max == other.Max));

    public override bool Equals(object? obj) => obj is CadBounds3D other && Equals(other);
    public override int GetHashCode() => IsEmpty ? 0 : HashCode.Combine(Min, Max);
    public static bool operator ==(CadBounds3D left, CadBounds3D right) => left.Equals(right);
    public static bool operator !=(CadBounds3D left, CadBounds3D right) => !left.Equals(right);

    internal static CadBounds3D Circle(
        CadPoint3D center,
        CadCoordinateSystem basis,
        double radius)
    {
        CadPoint3D amplitude = new(
            radius * Math.Sqrt((basis.XAxis.X * basis.XAxis.X) + (basis.YAxis.X * basis.YAxis.X)),
            radius * Math.Sqrt((basis.XAxis.Y * basis.XAxis.Y) + (basis.YAxis.Y * basis.YAxis.Y)),
            radius * Math.Sqrt((basis.XAxis.Z * basis.XAxis.Z) + (basis.YAxis.Z * basis.YAxis.Z)));
        return new CadBounds3D(center - amplitude, center + amplitude);
    }

    internal static CadBounds3D Arc(
        CadPoint3D center,
        CadCoordinateSystem basis,
        double radius,
        double startAngle,
        double sweepAngle)
    {
        CadBounds3D bounds = FromPoint(basis.PointOnCircle(center, radius, startAngle))
            .Include(basis.PointOnCircle(center, radius, startAngle + sweepAngle));

        bounds = IncludeAxisExtrema(bounds, center, basis, radius, startAngle, sweepAngle, 0);
        bounds = IncludeAxisExtrema(bounds, center, basis, radius, startAngle, sweepAngle, 1);
        return IncludeAxisExtrema(bounds, center, basis, radius, startAngle, sweepAngle, 2);
    }

    internal static CadBounds3D EllipseArc(
        CadPoint3D center,
        CadPoint3D majorAxis,
        CadPoint3D minorAxis,
        double startParameter,
        double sweepParameter)
    {
        CadBounds3D bounds = FromPoint(EllipsePointAt(center, majorAxis, minorAxis, startParameter))
            .Include(EllipsePointAt(center, majorAxis, minorAxis, startParameter + sweepParameter));
        bounds = IncludeEllipseAxisExtrema(
            bounds,
            center,
            majorAxis,
            minorAxis,
            startParameter,
            sweepParameter,
            0);
        bounds = IncludeEllipseAxisExtrema(
            bounds,
            center,
            majorAxis,
            minorAxis,
            startParameter,
            sweepParameter,
            1);
        return IncludeEllipseAxisExtrema(
            bounds,
            center,
            majorAxis,
            minorAxis,
            startParameter,
            sweepParameter,
            2);
    }

    private static CadBounds3D IncludeEllipseAxisExtrema(
        CadBounds3D bounds,
        CadPoint3D center,
        CadPoint3D majorAxis,
        CadPoint3D minorAxis,
        double start,
        double sweep,
        int axis)
    {
        double major = Component(majorAxis, axis);
        double minor = Component(minorAxis, axis);
        double stationary = Math.Atan2(minor, major);
        if (ContainsAngle(start, sweep, stationary))
        {
            bounds = bounds.Include(EllipsePointAt(center, majorAxis, minorAxis, stationary));
        }

        stationary += Math.PI;
        if (ContainsAngle(start, sweep, stationary))
        {
            bounds = bounds.Include(EllipsePointAt(center, majorAxis, minorAxis, stationary));
        }

        return bounds;
    }

    private static CadPoint3D EllipsePointAt(
        CadPoint3D center,
        CadPoint3D majorAxis,
        CadPoint3D minorAxis,
        double parameter) =>
        center + (majorAxis * Math.Cos(parameter)) + (minorAxis * Math.Sin(parameter));

    private static CadBounds3D IncludeAxisExtrema(
        CadBounds3D bounds,
        CadPoint3D center,
        CadCoordinateSystem basis,
        double radius,
        double start,
        double sweep,
        int axis)
    {
        double x = Component(basis.XAxis, axis);
        double y = Component(basis.YAxis, axis);
        double stationary = Math.Atan2(y, x);
        if (ContainsAngle(start, sweep, stationary))
        {
            bounds = bounds.Include(basis.PointOnCircle(center, radius, stationary));
        }

        stationary += Math.PI;
        if (ContainsAngle(start, sweep, stationary))
        {
            bounds = bounds.Include(basis.PointOnCircle(center, radius, stationary));
        }

        return bounds;
    }

    private static bool ContainsAngle(double start, double sweep, double angle)
    {
        const double twoPi = Math.PI * 2.0;
        double relative = (angle - start) % twoPi;
        if (relative < 0.0)
        {
            relative += twoPi;
        }

        if (sweep >= 0.0)
        {
            return relative <= sweep + 1e-12;
        }

        double reverseRelative = (start - angle) % twoPi;
        if (reverseRelative < 0.0)
        {
            reverseRelative += twoPi;
        }

        return reverseRelative <= -sweep + 1e-12;
    }

    private static double Component(CadPoint3D point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };

    private static bool AreFinite(CadPoint3D point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}
