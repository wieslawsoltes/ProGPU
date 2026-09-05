using ProGPU.Vector;
using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Numerics;

namespace System.Drawing;

/// <summary>
/// Describes a retained, device-independent set of points used for clipping and hit testing.
/// </summary>
public sealed class Region : MarshalByRefObject, IDisposable
{
    private const uint DataMagic = 0x31524750; // "PGR1"
    private const byte DataVersion = 1;
    private const int MaximumTreeDepth = 256;
    private const int MaximumSerializedItems = 1_000_000;
    private const float InfiniteCoordinate = 4_194_304f;

    private RegionExpression _expression;
    private bool _disposed;

    public Region()
    {
        _expression = RegionExpression.Infinite;
    }

    public Region(Rectangle rect)
        : this(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height))
    {
    }

    public Region(RectangleF rect)
    {
        _expression = CreateRectangleExpression(rect);
    }

    public Region(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _expression = CreateGeometryExpression(path.Geometry);
    }

    public Region(RegionData rgnData)
    {
        ArgumentNullException.ThrowIfNull(rgnData);
        _expression = Deserialize(rgnData.AsSpan());
    }

    private Region(RegionExpression expression)
    {
        _expression = expression;
    }

    public Region Clone()
    {
        ThrowIfDisposed();
        return new Region(_expression);
    }

    public void MakeEmpty()
    {
        ThrowIfDisposed();
        _expression = RegionExpression.Empty;
    }

    public void MakeInfinite()
    {
        ThrowIfDisposed();
        _expression = RegionExpression.Infinite;
    }

    public void Intersect(Rectangle rect) => Intersect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
    public void Intersect(RectangleF rect) => Apply(CreateRectangleExpression(rect), PathBooleanOperation.Intersect);
    public void Intersect(GraphicsPath path) => Apply(CreatePathOperand(path), PathBooleanOperation.Intersect);
    public void Intersect(Region region) => Apply(CreateRegionOperand(region), PathBooleanOperation.Intersect);

    public void Union(Rectangle rect) => Union(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
    public void Union(RectangleF rect) => Apply(CreateRectangleExpression(rect), PathBooleanOperation.Union);
    public void Union(GraphicsPath path) => Apply(CreatePathOperand(path), PathBooleanOperation.Union);
    public void Union(Region region) => Apply(CreateRegionOperand(region), PathBooleanOperation.Union);

    public void Xor(Rectangle rect) => Xor(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
    public void Xor(RectangleF rect) => Apply(CreateRectangleExpression(rect), PathBooleanOperation.ExclusiveOr);
    public void Xor(GraphicsPath path) => Apply(CreatePathOperand(path), PathBooleanOperation.ExclusiveOr);
    public void Xor(Region region) => Apply(CreateRegionOperand(region), PathBooleanOperation.ExclusiveOr);

    public void Exclude(Rectangle rect) => Exclude(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
    public void Exclude(RectangleF rect) => Apply(CreateRectangleExpression(rect), PathBooleanOperation.Difference);
    public void Exclude(GraphicsPath path) => Apply(CreatePathOperand(path), PathBooleanOperation.Difference);
    public void Exclude(Region region) => Apply(CreateRegionOperand(region), PathBooleanOperation.Difference);

    public void Complement(Rectangle rect) => Complement(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
    public void Complement(RectangleF rect) => Apply(CreateRectangleExpression(rect), PathBooleanOperation.ReverseDifference);
    public void Complement(GraphicsPath path) => Apply(CreatePathOperand(path), PathBooleanOperation.ReverseDifference);
    public void Complement(Region region) => Apply(CreateRegionOperand(region), PathBooleanOperation.ReverseDifference);

    public void Translate(int dx, int dy) => Translate((float)dx, dy);

    public void Translate(float dx, float dy)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy))
        {
            throw new ArgumentException("Region translation must be finite.");
        }

        TransformCore(Matrix3x2.CreateTranslation(dx, dy));
    }

    public void Transform(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Matrix3x2 value = matrix.Value;
        if (!IsFinite(value))
        {
            throw new ArgumentException("Region transform must be finite.", nameof(matrix));
        }

        TransformCore(value);
    }

    public RectangleF GetBounds(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        ThrowIfDisposed();
        return TryGetBounds(_expression, out RectangleF bounds)
            ? bounds
            : RectangleF.Empty;
    }

    public bool IsEmpty(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        ThrowIfDisposed();
        if (_expression.Kind == RegionExpressionKind.Empty)
        {
            return true;
        }

        if (_expression.Kind == RegionExpressionKind.Infinite)
        {
            return false;
        }

        if (TryGetAxisAlignedScans(_expression, Matrix3x2.Identity, out RectangleF[] scans))
        {
            return scans.Length == 0;
        }

        // Curved retained regions do not currently have exact scan extraction.
        // Return true only when emptiness is provable; a false negative is safer
        // for clipping than accidentally discarding visible content.
        return IsKnownEmpty(_expression);
    }

    private static bool IsKnownEmpty(RegionExpression expression)
    {
        return expression.Kind switch
        {
            RegionExpressionKind.Empty => true,
            RegionExpressionKind.Infinite => false,
            RegionExpressionKind.Geometry => !TryGetBounds(expression, out RectangleF bounds)
                || bounds.Width <= 0f
                || bounds.Height <= 0f,
            RegionExpressionKind.Boolean => expression.Operation switch
            {
                PathBooleanOperation.Intersect =>
                    IsKnownEmpty(expression.Left!) || IsKnownEmpty(expression.Right!),
                PathBooleanOperation.Union =>
                    IsKnownEmpty(expression.Left!) && IsKnownEmpty(expression.Right!),
                PathBooleanOperation.ExclusiveOr =>
                    IsKnownEmpty(expression.Left!) && IsKnownEmpty(expression.Right!),
                PathBooleanOperation.Difference => IsKnownEmpty(expression.Left!),
                PathBooleanOperation.ReverseDifference => IsKnownEmpty(expression.Right!),
                _ => false
            },
            _ => false
        };
    }

    public bool IsInfinite(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        ThrowIfDisposed();
        return _expression.Kind == RegionExpressionKind.Infinite;
    }

    internal bool IsInfiniteForContext()
    {
        ThrowIfDisposed();
        return _expression.Kind == RegionExpressionKind.Infinite;
    }

    public bool IsVisible(int x, int y) => IsVisible((float)x, y);
    public bool IsVisible(float x, float y) => ContainsPoint(new Vector2(x, y));
    public bool IsVisible(Point point) => IsVisible(point.X, point.Y);
    public bool IsVisible(PointF point) => IsVisible(point.X, point.Y);
    public bool IsVisible(int x, int y, Graphics g) => IsVisible((float)x, y, g);
    public bool IsVisible(float x, float y, Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return IsVisible(x, y);
    }

    public bool IsVisible(Point point, Graphics g) => IsVisible(point.X, point.Y, g);
    public bool IsVisible(PointF point, Graphics g) => IsVisible(point.X, point.Y, g);
    public bool IsVisible(int x, int y, int width, int height) => IsVisible(new RectangleF(x, y, width, height));
    public bool IsVisible(float x, float y, float width, float height) => IsVisible(new RectangleF(x, y, width, height));
    public bool IsVisible(Rectangle rect) => IsVisible(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));

    public bool IsVisible(RectangleF rect)
    {
        ThrowIfDisposed();
        if (rect.Width <= 0 || rect.Height <= 0 || !IsFinite(rect))
        {
            return false;
        }

        if (TryGetAxisAlignedScans(_expression, Matrix3x2.Identity, out RectangleF[] scans))
        {
            return scans.Any(scan => Intersects(scan, rect));
        }

        // This is conservative for curved/rotated retained paths, which is the safe
        // contract for invalidation and clipping callers until exact scan extraction runs.
        return TryGetBounds(_expression, out RectangleF bounds) && Intersects(bounds, rect);
    }

    public bool IsVisible(int x, int y, int width, int height, Graphics g) =>
        IsVisible((float)x, y, width, height, g);

    public bool IsVisible(float x, float y, float width, float height, Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return IsVisible(x, y, width, height);
    }

    public bool IsVisible(Rectangle rect, Graphics g) =>
        IsVisible(rect.X, rect.Y, rect.Width, rect.Height, g);

    public bool IsVisible(RectangleF rect, Graphics g) =>
        IsVisible(rect.X, rect.Y, rect.Width, rect.Height, g);

    public RectangleF[] GetRegionScans(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ThrowIfDisposed();
        if (!TryGetAxisAlignedScans(_expression, matrix.Value, out RectangleF[] scans))
        {
            throw new NotSupportedException(
                "Exact region scans currently require axis-aligned retained rectangle operands.");
        }

        return scans;
    }

    public RegionData GetRegionData()
    {
        ThrowIfDisposed();
        return new RegionData(Serialize(_expression));
    }

    public bool Equals(Region region, Graphics g)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(g);
        ThrowIfDisposed();
        region.ThrowIfDisposed();

        try
        {
            RectangleF[] left = GetRegionScans(new Matrix());
            RectangleF[] right = region.GetRegionScans(new Matrix());
            return left.AsSpan().SequenceEqual(right);
        }
        catch (NotSupportedException)
        {
            return Serialize(_expression).AsSpan().SequenceEqual(Serialize(region._expression));
        }
    }

    public static Region FromHrgn(IntPtr hrgn) =>
        throw new PlatformNotSupportedException(
            "Native HRGN import requires a platform GDI adapter and is not available in the portable ProGPU backend.");

    public IntPtr GetHrgn(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        ThrowIfDisposed();
        throw new PlatformNotSupportedException(
            "Native HRGN export requires a platform GDI adapter and is not available in the portable ProGPU backend.");
    }

    public void ReleaseHrgn(IntPtr regionHandle)
    {
        ThrowIfDisposed();
        throw new PlatformNotSupportedException(
            "Native HRGN ownership requires a platform GDI adapter and is not available in the portable ProGPU backend.");
    }

    internal PathGeometry CreatePathGeometry(RectangleF universe)
    {
        ThrowIfDisposed();
        RegionExpression finite = ReplaceInfinite(
            _expression,
            CreateRectangleExpression(universe));
        return Lower(finite);
    }

    public void Dispose()
    {
        _disposed = true;
        _expression = RegionExpression.Empty;
    }

    private void Apply(RegionExpression operand, PathBooleanOperation operation)
    {
        ThrowIfDisposed();
        _expression = Combine(_expression, operand, operation);
    }

    private static RegionExpression CreatePathOperand(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CreateGeometryExpression(path.Geometry);
    }

    private static RegionExpression CreateRegionOperand(Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.ThrowIfDisposed();
        return region._expression;
    }

    private static RegionExpression CreateRectangleExpression(RectangleF rectangle) =>
        rectangle.Width <= 0 || rectangle.Height <= 0 || !IsFinite(rectangle)
            ? RegionExpression.Empty
            : CreateGeometryExpression(PrimitivePathGeometry.CreateRectangle(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height));

    private static RegionExpression CreateGeometryExpression(PathGeometry geometry)
    {
        PathGeometry snapshot = geometry.CreateTransformed(Matrix4x4.Identity);
        return snapshot.TryGetBounds(out _, out _)
            ? RegionExpression.FromGeometry(snapshot)
            : RegionExpression.Empty;
    }

    private static RegionExpression Combine(
        RegionExpression left,
        RegionExpression right,
        PathBooleanOperation operation)
    {
        if (operation == PathBooleanOperation.Difference)
        {
            if (left.Kind == RegionExpressionKind.Empty || right.Kind == RegionExpressionKind.Infinite)
                return RegionExpression.Empty;
            if (right.Kind == RegionExpressionKind.Empty)
                return left;
        }
        else if (operation == PathBooleanOperation.ReverseDifference)
        {
            if (right.Kind == RegionExpressionKind.Empty || left.Kind == RegionExpressionKind.Infinite)
                return RegionExpression.Empty;
            if (left.Kind == RegionExpressionKind.Empty)
                return right;
        }
        else if (operation == PathBooleanOperation.Intersect)
        {
            if (left.Kind == RegionExpressionKind.Empty || right.Kind == RegionExpressionKind.Empty)
                return RegionExpression.Empty;
            if (left.Kind == RegionExpressionKind.Infinite)
                return right;
            if (right.Kind == RegionExpressionKind.Infinite)
                return left;
        }
        else if (operation == PathBooleanOperation.Union)
        {
            if (left.Kind == RegionExpressionKind.Infinite || right.Kind == RegionExpressionKind.Infinite)
                return RegionExpression.Infinite;
            if (left.Kind == RegionExpressionKind.Empty)
                return right;
            if (right.Kind == RegionExpressionKind.Empty)
                return left;
        }
        else if (operation == PathBooleanOperation.ExclusiveOr)
        {
            if (left.Kind == RegionExpressionKind.Empty)
                return right;
            if (right.Kind == RegionExpressionKind.Empty)
                return left;
            if (left.Kind == RegionExpressionKind.Infinite && right.Kind == RegionExpressionKind.Infinite)
                return RegionExpression.Empty;
        }

        return RegionExpression.FromBoolean(left, right, operation);
    }

    private void TransformCore(Matrix3x2 transform)
    {
        ThrowIfDisposed();
        _expression = TransformExpression(_expression, ToMatrix4x4(transform));
    }

    private static RegionExpression TransformExpression(RegionExpression expression, Matrix4x4 transform) =>
        expression.Kind switch
        {
            RegionExpressionKind.Empty => expression,
            RegionExpressionKind.Infinite => expression,
            RegionExpressionKind.Geometry => RegionExpression.FromGeometry(
                expression.Geometry!.CreateTransformed(transform)),
            RegionExpressionKind.Boolean => RegionExpression.FromBoolean(
                TransformExpression(expression.Left!, transform),
                TransformExpression(expression.Right!, transform),
                expression.Operation),
            _ => throw new InvalidOperationException(),
        };

    private bool ContainsPoint(Vector2 point)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            return false;
        }

        return TryContainsPoint(_expression, point, depth: 0, out bool contains) && contains;
    }

    private static bool TryContainsPoint(
        RegionExpression expression,
        Vector2 point,
        int depth,
        out bool contains)
    {
        contains = false;
        if (depth >= MaximumTreeDepth)
            return false;

        switch (expression.Kind)
        {
            case RegionExpressionKind.Empty:
                return true;
            case RegionExpressionKind.Infinite:
                contains = true;
                return true;
            case RegionExpressionKind.Geometry:
                return PathGeometryHitTesting.TryContainsFill(
                    expression.Geometry,
                    point,
                    tolerance: 0.01f,
                    relativeTolerance: false,
                    out contains);
            case RegionExpressionKind.Boolean:
                if (!TryContainsPoint(expression.Left!, point, depth + 1, out bool left) ||
                    !TryContainsPoint(expression.Right!, point, depth + 1, out bool right))
                {
                    return false;
                }

                contains = expression.Operation switch
                {
                    PathBooleanOperation.Difference => left && !right,
                    PathBooleanOperation.Intersect => left && right,
                    PathBooleanOperation.Union => left || right,
                    PathBooleanOperation.ExclusiveOr => left != right,
                    PathBooleanOperation.ReverseDifference => right && !left,
                    _ => false,
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetBounds(RegionExpression expression, out RectangleF bounds)
    {
        switch (expression.Kind)
        {
            case RegionExpressionKind.Empty:
                bounds = RectangleF.Empty;
                return false;
            case RegionExpressionKind.Infinite:
                bounds = InfiniteBounds;
                return true;
            case RegionExpressionKind.Geometry:
                if (expression.Geometry!.TryGetBounds(out Vector2 min, out Vector2 max))
                {
                    bounds = RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y);
                    return true;
                }

                bounds = RectangleF.Empty;
                return false;
            case RegionExpressionKind.Boolean:
                bool hasLeft = TryGetBounds(expression.Left!, out RectangleF left);
                bool hasRight = TryGetBounds(expression.Right!, out RectangleF right);
                if (expression.Operation == PathBooleanOperation.Difference)
                {
                    bounds = left;
                    return hasLeft;
                }

                if (expression.Operation == PathBooleanOperation.ReverseDifference)
                {
                    bounds = right;
                    return hasRight;
                }

                if (expression.Operation == PathBooleanOperation.Intersect)
                {
                    if (!hasLeft || !hasRight || !TryIntersect(left, right, out bounds))
                    {
                        bounds = RectangleF.Empty;
                        return false;
                    }

                    return true;
                }

                if (hasLeft && hasRight)
                {
                    bounds = RectangleF.FromLTRB(
                        MathF.Min(left.Left, right.Left),
                        MathF.Min(left.Top, right.Top),
                        MathF.Max(left.Right, right.Right),
                        MathF.Max(left.Bottom, right.Bottom));
                    return true;
                }

                bounds = hasLeft ? left : right;
                return hasLeft || hasRight;
            default:
                bounds = RectangleF.Empty;
                return false;
        }
    }

    private static bool TryGetAxisAlignedScans(
        RegionExpression expression,
        Matrix3x2 transform,
        out RectangleF[] scans)
    {
        scans = Array.Empty<RectangleF>();
        if (!IsFinite(transform))
            return false;

        RegionExpression transformed = TransformExpression(expression, ToMatrix4x4(transform));
        var xCoordinates = new SortedSet<float>();
        var yCoordinates = new SortedSet<float>();
        if (!CollectRectangleEdges(transformed, xCoordinates, yCoordinates, depth: 0))
            return false;

        if (xCoordinates.Count < 2 || yCoordinates.Count < 2)
            return true;
        if ((long)xCoordinates.Count * yCoordinates.Count > MaximumSerializedItems)
            throw new InvalidOperationException("Region scan complexity exceeds the portable safety limit.");

        float[] xs = xCoordinates.ToArray();
        float[] ys = yCoordinates.ToArray();
        var bands = new List<RectangleF>();
        for (int y = 0; y < ys.Length - 1; y++)
        {
            float top = ys[y];
            float bottom = ys[y + 1];
            float? runLeft = null;
            for (int x = 0; x < xs.Length - 1; x++)
            {
                float left = xs[x];
                float right = xs[x + 1];
                Vector2 sample = new((left + right) * 0.5f, (top + bottom) * 0.5f);
                bool inside = TryContainsPoint(transformed, sample, depth: 0, out bool value) && value;
                if (inside && runLeft == null)
                {
                    runLeft = left;
                }

                if (runLeft != null && (!inside || x == xs.Length - 2))
                {
                    float runRight = inside && x == xs.Length - 2 ? right : left;
                    bands.Add(RectangleF.FromLTRB(runLeft.Value, top, runRight, bottom));
                    runLeft = null;
                }
            }
        }

        scans = MergeVerticalBands(bands);
        return true;
    }

    private static bool CollectRectangleEdges(
        RegionExpression expression,
        SortedSet<float> xs,
        SortedSet<float> ys,
        int depth)
    {
        if (depth >= MaximumTreeDepth)
            return false;

        switch (expression.Kind)
        {
            case RegionExpressionKind.Empty:
                return true;
            case RegionExpressionKind.Infinite:
                AddEdges(InfiniteBounds, xs, ys);
                return true;
            case RegionExpressionKind.Geometry:
                if (!PrimitivePathGeometry.TryGetAxisAlignedRectangleBounds(
                    expression.Geometry!,
                    out Vector2 min,
                    out Vector2 max))
                {
                    return false;
                }

                AddEdges(RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y), xs, ys);
                return true;
            case RegionExpressionKind.Boolean:
                return CollectRectangleEdges(expression.Left!, xs, ys, depth + 1) &&
                    CollectRectangleEdges(expression.Right!, xs, ys, depth + 1);
            default:
                return false;
        }
    }

    private static void AddEdges(RectangleF rectangle, SortedSet<float> xs, SortedSet<float> ys)
    {
        xs.Add(rectangle.Left);
        xs.Add(rectangle.Right);
        ys.Add(rectangle.Top);
        ys.Add(rectangle.Bottom);
    }

    private static RectangleF[] MergeVerticalBands(List<RectangleF> bands)
    {
        var result = new List<RectangleF>(bands.Count);
        foreach (RectangleF band in bands)
        {
            int match = result.FindLastIndex(candidate =>
                candidate.Left == band.Left && candidate.Width == band.Width &&
                candidate.Bottom == band.Top);
            if (match >= 0)
            {
                RectangleF previous = result[match];
                result[match] = RectangleF.FromLTRB(
                    previous.Left,
                    previous.Top,
                    previous.Right,
                    band.Bottom);
            }
            else
            {
                result.Add(band);
            }
        }

        return result.ToArray();
    }

    private static RegionExpression ReplaceInfinite(
        RegionExpression expression,
        RegionExpression universe) =>
        expression.Kind switch
        {
            RegionExpressionKind.Infinite => universe,
            RegionExpressionKind.Boolean => RegionExpression.FromBoolean(
                ReplaceInfinite(expression.Left!, universe),
                ReplaceInfinite(expression.Right!, universe),
                expression.Operation),
            _ => expression,
        };

    private static PathGeometry Lower(RegionExpression expression) =>
        expression.Kind switch
        {
            RegionExpressionKind.Empty => new PathGeometry(),
            RegionExpressionKind.Geometry => expression.Geometry!,
            RegionExpressionKind.Boolean => PathGeometry.CombineDeferred(
                Lower(expression.Left!),
                Lower(expression.Right!),
                expression.Operation),
            _ => throw new InvalidOperationException("An infinite region must be bounded before lowering."),
        };

    private static byte[] Serialize(RegionExpression expression)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(DataMagic);
        writer.Write(DataVersion);
        WriteExpression(writer, expression, depth: 0);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteExpression(BinaryWriter writer, RegionExpression expression, int depth)
    {
        if (depth >= MaximumTreeDepth)
            throw new InvalidOperationException("Region expression exceeds the serialization depth limit.");

        writer.Write((byte)expression.Kind);
        switch (expression.Kind)
        {
            case RegionExpressionKind.Geometry:
                WriteGeometry(writer, expression.Geometry!);
                break;
            case RegionExpressionKind.Boolean:
                writer.Write((byte)expression.Operation);
                WriteExpression(writer, expression.Left!, depth + 1);
                WriteExpression(writer, expression.Right!, depth + 1);
                break;
        }
    }

    private static void WriteGeometry(BinaryWriter writer, PathGeometry geometry)
    {
        writer.Write((byte)geometry.FillRule);
        writer.Write(geometry.Figures.Count);
        foreach (PathFigure figure in geometry.Figures)
        {
            writer.Write(figure.StartPoint.X);
            writer.Write(figure.StartPoint.Y);
            writer.Write(figure.IsClosed);
            writer.Write(figure.IsFilled);
            writer.Write(figure.Segments.Count);
            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        writer.Write((byte)1);
                        WriteSegmentFlags(writer, line);
                        WriteVector(writer, line.Point);
                        break;
                    case QuadraticBezierSegment quadratic:
                        writer.Write((byte)2);
                        WriteSegmentFlags(writer, quadratic);
                        WriteVector(writer, quadratic.ControlPoint);
                        WriteVector(writer, quadratic.Point);
                        break;
                    case CubicBezierSegment cubic:
                        writer.Write((byte)3);
                        WriteSegmentFlags(writer, cubic);
                        WriteVector(writer, cubic.ControlPoint1);
                        WriteVector(writer, cubic.ControlPoint2);
                        WriteVector(writer, cubic.Point);
                        break;
                    case ArcSegment arc:
                        writer.Write((byte)4);
                        WriteSegmentFlags(writer, arc);
                        WriteVector(writer, arc.Point);
                        WriteVector(writer, arc.Size);
                        writer.Write(arc.RotationAngle);
                        writer.Write(arc.IsLargeArc);
                        writer.Write((byte)arc.SweepDirection);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported retained segment type {segment.GetType().FullName}.");
                }
            }
        }
    }

    private static void WriteSegmentFlags(BinaryWriter writer, PathSegment segment)
    {
        writer.Write(segment.IsSmoothJoin);
        writer.Write(segment.IsStroked);
    }

    private static void WriteVector(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static RegionExpression Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != DataMagic || reader.ReadByte() != DataVersion)
            {
                throw new ArgumentException("RegionData does not contain a supported portable ProGPU region.", nameof(data));
            }

            RegionExpression expression = ReadExpression(reader, depth: 0, refCount: new ItemCounter());
            if (stream.Position != stream.Length)
                throw new ArgumentException("RegionData contains trailing data.", nameof(data));
            return expression;
        }
        catch (EndOfStreamException exception)
        {
            throw new ArgumentException("RegionData is truncated.", nameof(data), exception);
        }
        catch (IOException exception)
        {
            throw new ArgumentException("RegionData could not be read.", nameof(data), exception);
        }
    }

    private static RegionExpression ReadExpression(BinaryReader reader, int depth, ItemCounter refCount)
    {
        refCount.Increment();
        if (depth >= MaximumTreeDepth)
            throw new ArgumentException("RegionData exceeds the expression depth limit.");

        RegionExpressionKind kind = (RegionExpressionKind)reader.ReadByte();
        return kind switch
        {
            RegionExpressionKind.Empty => RegionExpression.Empty,
            RegionExpressionKind.Infinite => RegionExpression.Infinite,
            RegionExpressionKind.Geometry => RegionExpression.FromGeometry(ReadGeometry(reader, refCount)),
            RegionExpressionKind.Boolean => ReadBooleanExpression(reader, depth, refCount),
            _ => throw new ArgumentException("RegionData contains an unknown expression kind."),
        };
    }

    private static RegionExpression ReadBooleanExpression(BinaryReader reader, int depth, ItemCounter refCount)
    {
        PathBooleanOperation operation = (PathBooleanOperation)reader.ReadByte();
        if (operation < PathBooleanOperation.Difference ||
            operation > PathBooleanOperation.ReverseDifference)
        {
            throw new ArgumentException("RegionData contains an unknown boolean operation.");
        }

        RegionExpression left = ReadExpression(reader, depth + 1, refCount);
        RegionExpression right = ReadExpression(reader, depth + 1, refCount);
        return Combine(left, right, operation);
    }

    private static PathGeometry ReadGeometry(BinaryReader reader, ItemCounter refCount)
    {
        FillRule fillRule = (FillRule)reader.ReadByte();
        if (fillRule < FillRule.EvenOdd || fillRule > FillRule.Nonzero)
            throw new ArgumentException("RegionData contains an unknown fill rule.");

        var geometry = new PathGeometry { FillRule = fillRule };
        int figureCount = ReadCount(reader, refCount);
        for (int figureIndex = 0; figureIndex < figureCount; figureIndex++)
        {
            refCount.Increment();
            var figure = new PathFigure(ReadVector(reader), reader.ReadBoolean())
            {
                IsFilled = reader.ReadBoolean(),
            };
            int segmentCount = ReadCount(reader, refCount);
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                refCount.Increment();
                byte kind = reader.ReadByte();
                bool smooth = reader.ReadBoolean();
                bool stroked = reader.ReadBoolean();
                PathSegment segment = kind switch
                {
                    1 => new LineSegment(ReadVector(reader), smooth, stroked),
                    2 => new QuadraticBezierSegment(ReadVector(reader), ReadVector(reader), smooth, stroked),
                    3 => new CubicBezierSegment(ReadVector(reader), ReadVector(reader), ReadVector(reader), smooth, stroked),
                    4 => ReadArc(reader, smooth, stroked),
                    _ => throw new ArgumentException("RegionData contains an unknown path segment."),
                };
                figure.Segments.Add(segment);
            }

            geometry.Figures.Add(figure);
        }

        return geometry;
    }

    private static ArcSegment ReadArc(BinaryReader reader, bool smooth, bool stroked)
    {
        Vector2 point = ReadVector(reader);
        Vector2 size = ReadVector(reader);
        float rotation = reader.ReadSingle();
        bool large = reader.ReadBoolean();
        SweepDirection sweep = (SweepDirection)reader.ReadByte();
        if (sweep < SweepDirection.Counterclockwise || sweep > SweepDirection.Clockwise)
            throw new ArgumentException("RegionData contains an unknown arc sweep direction.");
        return new ArcSegment(point, size, rotation, large, sweep, smooth, stroked);
    }

    private static int ReadCount(BinaryReader reader, ItemCounter refCount)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumSerializedItems)
            throw new ArgumentException("RegionData contains an invalid item count.");
        refCount.Add(count);
        return count;
    }

    private static Vector2 ReadVector(BinaryReader reader)
    {
        var value = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentException("RegionData contains a non-finite coordinate.");
        return value;
    }

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32);

    private static bool IsFinite(RectangleF rectangle) =>
        float.IsFinite(rectangle.X) && float.IsFinite(rectangle.Y) &&
        float.IsFinite(rectangle.Width) && float.IsFinite(rectangle.Height) &&
        float.IsFinite(rectangle.Right) && float.IsFinite(rectangle.Bottom);

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix) => new(
        matrix.M11, matrix.M12, 0f, 0f,
        matrix.M21, matrix.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        matrix.M31, matrix.M32, 0f, 1f);

    private static bool Intersects(RectangleF left, RectangleF right) =>
        left.Left < right.Right && right.Left < left.Right &&
        left.Top < right.Bottom && right.Top < left.Bottom;

    private static bool TryIntersect(RectangleF left, RectangleF right, out RectangleF result)
    {
        float x1 = MathF.Max(left.Left, right.Left);
        float y1 = MathF.Max(left.Top, right.Top);
        float x2 = MathF.Min(left.Right, right.Right);
        float y2 = MathF.Min(left.Bottom, right.Bottom);
        if (x2 <= x1 || y2 <= y1)
        {
            result = RectangleF.Empty;
            return false;
        }

        result = RectangleF.FromLTRB(x1, y1, x2, y2);
        return true;
    }

    private static RectangleF InfiniteBounds => RectangleF.FromLTRB(
        -InfiniteCoordinate,
        -InfiniteCoordinate,
        InfiniteCoordinate,
        InfiniteCoordinate);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum RegionExpressionKind : byte
    {
        Empty = 0,
        Infinite = 1,
        Geometry = 2,
        Boolean = 3,
    }

    private sealed class RegionExpression
    {
        public static readonly RegionExpression Empty = new(RegionExpressionKind.Empty);
        public static readonly RegionExpression Infinite = new(RegionExpressionKind.Infinite);

        private RegionExpression(RegionExpressionKind kind)
        {
            Kind = kind;
        }

        public RegionExpressionKind Kind { get; }
        public PathGeometry? Geometry { get; private init; }
        public RegionExpression? Left { get; private init; }
        public RegionExpression? Right { get; private init; }
        public PathBooleanOperation Operation { get; private init; }

        public static RegionExpression FromGeometry(PathGeometry geometry) =>
            new(RegionExpressionKind.Geometry) { Geometry = geometry };

        public static RegionExpression FromBoolean(
            RegionExpression left,
            RegionExpression right,
            PathBooleanOperation operation) =>
            new(RegionExpressionKind.Boolean)
            {
                Left = left,
                Right = right,
                Operation = operation,
            };
    }

    private sealed class ItemCounter
    {
        private int _value;

        public void Increment() => Add(1);

        public void Add(int count)
        {
            _value = checked(_value + count);
            if (_value > MaximumSerializedItems)
                throw new ArgumentException("RegionData exceeds the portable item limit.");
        }
    }
}
