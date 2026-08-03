using System.Numerics;
using System.Runtime.CompilerServices;

namespace SkiaSharp;

[Flags]
public enum SKPathMeasureMatrixFlags
{
    GetPosition = 0x01,
    GetTangent = 0x02,
    GetPositionAndTangent = GetPosition | GetTangent
}

public class SKPathMeasure : SKObject
{
    private const float Epsilon = 0.00001f;
    private const int MaximumRetainedContours = 64;
    private const int MaximumRetainedSegments = 2_048;
    private const int MaximumRetainedSamples = 8_192;

    [ThreadStatic]
    private static ContourStorage? s_threadStorage;

    private readonly int _curveSegmentCount;
    private ContourStorage? _storage;
    private int _contourIndex;
    private Contour? _contour;

    public SKPathMeasure()
        : this(null, false, 1f)
    {
    }

    public SKPathMeasure(SKPath? path, bool forceClosed = false, float resScale = 1f)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _storage = RentStorage();
        var normalizedScale = float.IsFinite(resScale) && resScale > 0f ? resScale : 1f;
        // Skia's precision grows sublinearly with resScale. The square-root schedule
        // tracks its contour lengths while bounding setup work for text-on-path use.
        _curveSegmentCount = Math.Clamp(
            1 + (int)MathF.Ceiling(4f * MathF.Sqrt(normalizedScale)),
            2,
            64);
        SetPath(path, forceClosed);
    }

    public float Length => _contour?.Length ?? 0f;

    public bool IsClosed => _contour?.IsClosed ?? false;

    public void SetPath(SKPath? path) => SetPath(path, forceClosed: false);

    public void SetPath(SKPath? path, bool forceClosed)
    {
        var storage = _storage ?? throw new ObjectDisposedException(nameof(SKPathMeasure));
        storage.Reset();
        if (path is not null)
        {
            BuildContours(path, forceClosed, storage);
        }

        _contourIndex = 0;
        _contour = storage.Count == 0 ? null : storage[0];
    }

    public bool NextContour()
    {
        var storage = _storage;
        if (storage is null || _contourIndex + 1 >= storage.Count)
        {
            _contour = null;
            return false;
        }

        _contour = storage[++_contourIndex];
        return true;
    }

    public SKPoint GetPosition(float distance) =>
        GetPosition(distance, out var position) ? position : SKPoint.Empty;

    public bool GetPosition(float distance, out SKPoint position)
    {
        if (!TryGetSample(distance, out var point, out _))
        {
            Unsafe.SkipInit(out position);
            return false;
        }

        position = ToPoint(point);
        return true;
    }

    public SKPoint GetTangent(float distance) =>
        GetTangent(distance, out var tangent) ? tangent : SKPoint.Empty;

    public bool GetTangent(float distance, out SKPoint tangent)
    {
        if (!TryGetSample(distance, out _, out var direction))
        {
            Unsafe.SkipInit(out tangent);
            return false;
        }

        tangent = ToPoint(direction);
        return true;
    }

    public bool GetPositionAndTangent(float distance, out SKPoint position, out SKPoint tangent)
    {
        if (!TryGetSample(distance, out var point, out var direction))
        {
            Unsafe.SkipInit(out position);
            Unsafe.SkipInit(out tangent);
            return false;
        }

        position = ToPoint(point);
        tangent = ToPoint(direction);
        return true;
    }

    public SKMatrix GetMatrix(float distance, SKPathMeasureMatrixFlags flags) =>
        GetMatrix(distance, out var matrix, flags) ? matrix : SKMatrix.Empty;

    public bool GetMatrix(float distance, out SKMatrix matrix, SKPathMeasureMatrixFlags flags)
    {
        if (!TryGetSample(distance, out var point, out var tangent))
        {
            matrix = SKMatrix.Identity;
            return false;
        }

        matrix = SKMatrix.Identity;
        if ((flags & SKPathMeasureMatrixFlags.GetTangent) != 0)
        {
            matrix.ScaleX = tangent.X;
            matrix.SkewX = -tangent.Y;
            matrix.SkewY = tangent.Y;
            matrix.ScaleY = tangent.X;
        }

        if ((flags & SKPathMeasureMatrixFlags.GetPosition) != 0)
        {
            matrix.TransX = point.X;
            matrix.TransY = point.Y;
        }

        return true;
    }

    public SKPath? GetSegment(float start, float stop, bool startWithMoveTo)
    {
        using var builder = new SKPathBuilder();
        return GetSegment(start, stop, builder, startWithMoveTo)
            ? builder.Detach()
            : null;
    }

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetSegment(float start, float stop, SKPath dst, bool startWithMoveTo)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var builder = new SKPathBuilder();
        if (!GetSegment(start, stop, builder, startWithMoveTo))
        {
            return false;
        }

        using var segment = builder.Detach();
        dst.Reset();
        dst.AddPath(segment);
        return true;
    }

    public bool GetSegment(float start, float stop, SKPathBuilder dst, bool startWithMoveTo)
    {
        ArgumentNullException.ThrowIfNull(dst);
        var contour = _contour;
        if (contour is null ||
            contour.Length <= Epsilon ||
            !float.IsFinite(start) ||
            !float.IsFinite(stop) ||
            start > stop ||
            stop < 0f ||
            start > contour.Length)
        {
            return false;
        }

        var clampedStart = Math.Clamp(start, 0f, contour.Length);
        var clampedStop = Math.Clamp(stop, 0f, contour.Length);
        var startLocation = contour.GetLocation(clampedStart);
        var stopLocation = contour.GetLocation(clampedStop);
        var startPoint = contour.GetPoint(startLocation);
        var destinationWasEmpty = dst.IsEmpty;

        if (startWithMoveTo)
        {
            dst.MoveTo(ToPoint(startPoint));
        }
        else if (!destinationWasEmpty)
        {
            dst.LineTo(ToPoint(startPoint));
        }
        if (MathF.Abs(clampedStop - clampedStart) <= Epsilon)
        {
            if (startWithMoveTo || destinationWasEmpty)
            {
                dst.LineTo(ToPoint(startPoint));
            }

            return true;
        }

        var firstSegment = startLocation.SegmentIndex;
        var firstParameter = startLocation.Parameter;
        if (firstParameter >= 1f - Epsilon && firstSegment + 1 < contour.Segments.Count)
        {
            firstSegment++;
            firstParameter = 0f;
        }

        for (var segmentIndex = firstSegment;
             segmentIndex <= stopLocation.SegmentIndex;
             segmentIndex++)
        {
            var startParameter = segmentIndex == firstSegment ? firstParameter : 0f;
            var stopParameter = segmentIndex == stopLocation.SegmentIndex
                ? stopLocation.Parameter
                : 1f;
            if (stopParameter > startParameter + Epsilon)
            {
                contour.Segments[segmentIndex].Append(dst, startParameter, stopParameter);
            }
        }

        return true;
    }

    private bool TryGetSample(float distance, out Vector2 point, out Vector2 tangent)
    {
        var contour = _contour;
        if (contour is null || contour.Length <= Epsilon || !float.IsFinite(distance))
        {
            point = default;
            tangent = default;
            return false;
        }

        var location = contour.GetLocation(Math.Clamp(distance, 0f, contour.Length));
        point = contour.GetPoint(location);
        tangent = contour.GetTangent(location);
        return tangent.LengthSquared() > Epsilon * Epsilon;
    }

    private void BuildContours(SKPath path, bool forceClosed, ContourStorage storage)
    {
        if (path.PackedPathData is { } packed)
        {
            BuildPackedContours(packed.CommandSpan, forceClosed, storage);
            return;
        }

        using var iterator = path.CreateRawIterator();
        Span<SKPoint> points = stackalloc SKPoint[4];
        Contour? contour = null;
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    FinalizeContour(contour, forceClosed);
                    contour = storage.AddContour(ToVector(points[0]));
                    break;

                case SKPathVerb.Line:
                    contour ??= storage.AddContour(ToVector(points[0]));
                    contour.AddSegment(MeasuredSegment.Line(ToVector(points[0]), ToVector(points[1])), 1);
                    break;

                case SKPathVerb.Quad:
                    contour ??= storage.AddContour(ToVector(points[0]));
                    contour.AddSegment(
                        MeasuredSegment.Quad(ToVector(points[0]), ToVector(points[1]), ToVector(points[2])),
                        _curveSegmentCount);
                    break;

                case SKPathVerb.Conic:
                    contour ??= storage.AddContour(ToVector(points[0]));
                    contour.AddSegment(
                        MeasuredSegment.Conic(
                            ToVector(points[0]),
                            ToVector(points[1]),
                            ToVector(points[2]),
                            iterator.ConicWeight()),
                        _curveSegmentCount);
                    break;

                case SKPathVerb.Cubic:
                    contour ??= storage.AddContour(ToVector(points[0]));
                    contour.AddSegment(
                        MeasuredSegment.Cubic(
                            ToVector(points[0]),
                            ToVector(points[1]),
                            ToVector(points[2]),
                            ToVector(points[3])),
                        _curveSegmentCount);
                    break;

                case SKPathVerb.Close:
                    if (contour is not null)
                    {
                        contour.Close();
                    }
                    break;
            }
        }

        FinalizeContour(contour, forceClosed);
    }

    private void BuildPackedContours(
        ReadOnlySpan<PackedPathCommand> commands,
        bool forceClosed,
        ContourStorage storage)
    {
        Contour? contour = null;
        var current = Vector2.Zero;
        foreach (ref readonly var command in commands)
        {
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                    FinalizeContour(contour, forceClosed);
                    current = command.Point0;
                    contour = storage.AddContour(current);
                    break;

                case PackedPathCommandKind.Line:
                    contour ??= storage.AddContour(current);
                    contour.AddSegment(MeasuredSegment.Line(current, command.Point0), 1);
                    current = command.Point0;
                    break;

                case PackedPathCommandKind.Quadratic:
                    contour ??= storage.AddContour(current);
                    contour.AddSegment(
                        MeasuredSegment.Quad(current, command.Point0, command.Point1),
                        _curveSegmentCount);
                    current = command.Point1;
                    break;

                case PackedPathCommandKind.Cubic:
                    contour ??= storage.AddContour(current);
                    contour.AddSegment(
                        MeasuredSegment.Cubic(current, command.Point0, command.Point1, command.Point2),
                        _curveSegmentCount);
                    current = command.Point2;
                    break;

                case PackedPathCommandKind.Close:
                    contour?.Close();
                    break;
            }
        }

        FinalizeContour(contour, forceClosed);
    }

    private static void FinalizeContour(Contour? contour, bool forceClosed)
    {
        if (contour is null)
        {
            return;
        }

        if (forceClosed)
        {
            contour.Close();
        }

    }

    protected override void DisposeManaged()
    {
        _contour = null;
        if (_storage is { } storage)
        {
            _storage = null;
            storage.Reset();
            if (s_threadStorage is null && storage.CanRetain)
            {
                s_threadStorage = storage;
            }
        }
        base.DisposeManaged();
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private static SKPoint ToPoint(Vector2 value) => new(value.X, value.Y);

    private static Vector2 ToVector(SKPoint value) => new(value.X, value.Y);

    private static ContourStorage RentStorage()
    {
        var storage = s_threadStorage;
        if (storage is null)
        {
            return new ContourStorage();
        }

        s_threadStorage = null;
        return storage;
    }

    private readonly record struct Sample(
        Vector2 Point,
        float Distance,
        int SegmentIndex,
        float Parameter);

    private readonly record struct SegmentLocation(int SegmentIndex, float Parameter);

    private sealed class Contour
    {
        private Vector2 _start;
        private readonly List<Sample> _samples = new();

        public Contour(Vector2 start)
        {
            _start = start;
        }

        public int RetainedSegmentCapacity => Segments.Capacity;

        public int RetainedSampleCapacity => _samples.Capacity;

        public List<MeasuredSegment> Segments { get; } = new();

        public bool IsClosed { get; private set; }

        public float Length => _samples.Count == 0 ? 0f : _samples[^1].Distance;

        public void Reset(Vector2 start)
        {
            _start = start;
            _samples.Clear();
            Segments.Clear();
            IsClosed = false;
        }

        public void AddSegment(MeasuredSegment segment, int subdivisionCount)
        {
            var segmentIndex = Segments.Count;
            Segments.Add(segment);
            var previous = _samples.Count == 0 ? segment.Start : _samples[^1].Point;
            var distance = Length;
            for (var step = 1; step <= subdivisionCount; step++)
            {
                var parameter = step / (float)subdivisionCount;
                var point = segment.Evaluate(parameter);
                var stepLength = Vector2.Distance(previous, point);
                if (float.IsFinite(stepLength) && stepLength > Epsilon)
                {
                    distance += stepLength;
                    _samples.Add(new Sample(point, distance, segmentIndex, parameter));
                    previous = point;
                }
            }
        }

        public void Close()
        {
            if (IsClosed)
            {
                return;
            }

            var current = Segments.Count == 0 ? _start : Segments[^1].End;
            if (Vector2.DistanceSquared(current, _start) > Epsilon * Epsilon)
            {
                AddSegment(MeasuredSegment.Line(current, _start), 1);
            }

            IsClosed = true;
        }

        public SegmentLocation GetLocation(float distance)
        {
            if (_samples.Count == 0)
            {
                return default;
            }

            if (distance <= 0f)
            {
                return new SegmentLocation(_samples[0].SegmentIndex, 0f);
            }

            if (distance >= Length)
            {
                var last = _samples[^1];
                return new SegmentLocation(last.SegmentIndex, 1f);
            }

            var low = 0;
            var high = _samples.Count - 1;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_samples[middle].Distance < distance)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            var upper = _samples[low];
            var lowerDistance = low == 0 ? 0f : _samples[low - 1].Distance;
            var lowerParameter = low > 0 && _samples[low - 1].SegmentIndex == upper.SegmentIndex
                ? _samples[low - 1].Parameter
                : 0f;
            var spanLength = upper.Distance - lowerDistance;
            var amount = spanLength <= Epsilon ? 0f : (distance - lowerDistance) / spanLength;
            return new SegmentLocation(
                upper.SegmentIndex,
                Math.Clamp(float.Lerp(lowerParameter, upper.Parameter, amount), 0f, 1f));
        }

        public Vector2 GetPoint(SegmentLocation location) =>
            Segments[location.SegmentIndex].Evaluate(location.Parameter);

        public Vector2 GetTangent(SegmentLocation location)
        {
            var tangent = Segments[location.SegmentIndex].Tangent(location.Parameter);
            if (tangent.LengthSquared() <= Epsilon * Epsilon)
            {
                return Vector2.Zero;
            }

            return Vector2.Normalize(tangent);
        }
    }

    private sealed class ContourStorage
    {
        private readonly List<Contour> _contours = new(4);

        public int Count { get; private set; }

        public Contour this[int index] => _contours[index];

        public bool CanRetain
        {
            get
            {
                if (_contours.Capacity > MaximumRetainedContours)
                {
                    return false;
                }

                var segments = 0;
                var samples = 0;
                foreach (var contour in _contours)
                {
                    segments += contour.RetainedSegmentCapacity;
                    samples += contour.RetainedSampleCapacity;
                }

                return segments <= MaximumRetainedSegments &&
                       samples <= MaximumRetainedSamples;
            }
        }

        public Contour AddContour(Vector2 start)
        {
            Contour contour;
            if (Count < _contours.Count)
            {
                contour = _contours[Count];
                contour.Reset(start);
            }
            else
            {
                contour = new Contour(start);
                _contours.Add(contour);
            }

            Count++;
            return contour;
        }

        public void Reset()
        {
            for (var index = 0; index < Count; index++)
            {
                _contours[index].Reset(default);
            }

            Count = 0;
        }
    }

    private readonly struct MeasuredSegment
    {
        private MeasuredSegment(
            SKPathVerb verb,
            Vector2 point0,
            Vector2 point1,
            Vector2 point2,
            Vector2 point3,
            float weight)
        {
            Verb = verb;
            Point0 = point0;
            Point1 = point1;
            Point2 = point2;
            Point3 = point3;
            Weight = weight;
        }

        public SKPathVerb Verb { get; }

        public Vector2 Point0 { get; }

        public Vector2 Point1 { get; }

        public Vector2 Point2 { get; }

        public Vector2 Point3 { get; }

        public float Weight { get; }

        public Vector2 Start => Point0;

        public Vector2 End => Verb switch
        {
            SKPathVerb.Line => Point1,
            SKPathVerb.Quad or SKPathVerb.Conic => Point2,
            SKPathVerb.Cubic => Point3,
            _ => Point0,
        };

        public static MeasuredSegment Line(Vector2 point0, Vector2 point1) =>
            new(SKPathVerb.Line, point0, point1, default, default, 1f);

        public static MeasuredSegment Quad(Vector2 point0, Vector2 point1, Vector2 point2) =>
            new(SKPathVerb.Quad, point0, point1, point2, default, 1f);

        public static MeasuredSegment Conic(Vector2 point0, Vector2 point1, Vector2 point2, float weight) =>
            new(SKPathVerb.Conic, point0, point1, point2, default, weight);

        public static MeasuredSegment Cubic(
            Vector2 point0,
            Vector2 point1,
            Vector2 point2,
            Vector2 point3) =>
            new(SKPathVerb.Cubic, point0, point1, point2, point3, 1f);

        public Vector2 Evaluate(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Verb switch
            {
                SKPathVerb.Line => Vector2.Lerp(Point0, Point1, t),
                SKPathVerb.Quad => EvaluateQuadratic(Point0, Point1, Point2, t),
                SKPathVerb.Conic => EvaluateConic(Point0, Point1, Point2, Weight, t),
                SKPathVerb.Cubic => EvaluateCubic(Point0, Point1, Point2, Point3, t),
                _ => Point0,
            };
        }

        public Vector2 Tangent(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Verb switch
            {
                SKPathVerb.Line => Point1 - Point0,
                SKPathVerb.Quad =>
                    2f * ((1f - t) * (Point1 - Point0) + t * (Point2 - Point1)),
                SKPathVerb.Conic => EvaluateConicTangent(Point0, Point1, Point2, Weight, t),
                SKPathVerb.Cubic => EvaluateCubicTangent(Point0, Point1, Point2, Point3, t),
                _ => Vector2.Zero,
            };
        }

        public void Append(SKPathBuilder destination, float startParameter, float stopParameter)
        {
            startParameter = Math.Clamp(startParameter, 0f, 1f);
            stopParameter = Math.Clamp(stopParameter, 0f, 1f);
            if (startParameter <= Epsilon && stopParameter >= 1f - Epsilon)
            {
                AppendFull(destination);
                return;
            }

            switch (Verb)
            {
                case SKPathVerb.Line:
                    destination.LineTo(ToPoint(Evaluate(stopParameter)));
                    break;

                case SKPathVerb.Quad:
                    SliceQuadratic(Point0, Point1, Point2, startParameter, stopParameter, out _, out var q1, out var q2);
                    destination.QuadTo(ToPoint(q1), ToPoint(q2));
                    break;

                case SKPathVerb.Conic:
                    if (TrySliceConic(
                            Point0,
                            Point1,
                            Point2,
                            Weight,
                            startParameter,
                            stopParameter,
                            out _,
                            out var c1,
                            out var c2,
                            out var weight))
                    {
                        destination.ConicTo(ToPoint(c1), ToPoint(c2), weight);
                    }
                    else
                    {
                        destination.LineTo(ToPoint(Evaluate(stopParameter)));
                    }
                    break;

                case SKPathVerb.Cubic:
                    SliceCubic(
                        Point0,
                        Point1,
                        Point2,
                        Point3,
                        startParameter,
                        stopParameter,
                        out _,
                        out var b1,
                        out var b2,
                        out var b3);
                    destination.CubicTo(ToPoint(b1), ToPoint(b2), ToPoint(b3));
                    break;
            }
        }

        private void AppendFull(SKPathBuilder destination)
        {
            switch (Verb)
            {
                case SKPathVerb.Line:
                    destination.LineTo(ToPoint(Point1));
                    break;
                case SKPathVerb.Quad:
                    destination.QuadTo(ToPoint(Point1), ToPoint(Point2));
                    break;
                case SKPathVerb.Conic:
                    destination.ConicTo(ToPoint(Point1), ToPoint(Point2), Weight);
                    break;
                case SKPathVerb.Cubic:
                    destination.CubicTo(ToPoint(Point1), ToPoint(Point2), ToPoint(Point3));
                    break;
            }
        }

        private static Vector2 EvaluateQuadratic(Vector2 p0, Vector2 p1, Vector2 p2, float t)
        {
            var oneMinusT = 1f - t;
            return oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2;
        }

        private static Vector2 EvaluateCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            var oneMinusT = 1f - t;
            return oneMinusT * oneMinusT * oneMinusT * p0 +
                   3f * oneMinusT * oneMinusT * t * p1 +
                   3f * oneMinusT * t * t * p2 +
                   t * t * t * p3;
        }

        private static Vector2 EvaluateCubicTangent(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            float t)
        {
            var oneMinusT = 1f - t;
            return 3f * oneMinusT * oneMinusT * (p1 - p0) +
                   6f * oneMinusT * t * (p2 - p1) +
                   3f * t * t * (p3 - p2);
        }

        private static Vector2 EvaluateConic(Vector2 p0, Vector2 p1, Vector2 p2, float weight, float t)
        {
            var oneMinusT = 1f - t;
            var b0 = oneMinusT * oneMinusT;
            var b1 = 2f * weight * oneMinusT * t;
            var b2 = t * t;
            var denominator = b0 + b1 + b2;
            return MathF.Abs(denominator) <= Epsilon
                ? p2
                : (b0 * p0 + b1 * p1 + b2 * p2) / denominator;
        }

        private static Vector2 EvaluateConicTangent(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            float weight,
            float t)
        {
            var oneMinusT = 1f - t;
            var numerator = oneMinusT * oneMinusT * p0 +
                            2f * weight * oneMinusT * t * p1 +
                            t * t * p2;
            var denominator = oneMinusT * oneMinusT + 2f * weight * oneMinusT * t + t * t;
            if (MathF.Abs(denominator) <= Epsilon)
            {
                return Vector2.Zero;
            }

            var numeratorDerivative =
                2f * (oneMinusT * (weight * p1 - p0) + t * (p2 - weight * p1));
            var denominatorDerivative =
                2f * (oneMinusT * (weight - 1f) + t * (1f - weight));
            return (numeratorDerivative * denominator - numerator * denominatorDerivative) /
                   (denominator * denominator);
        }

        private static void SliceQuadratic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            float start,
            float stop,
            out Vector2 q0,
            out Vector2 q1,
            out Vector2 q2)
        {
            SplitQuadratic(p0, p1, p2, stop, out var l0, out var l1, out var l2, out _, out _, out _);
            if (start <= Epsilon)
            {
                q0 = l0;
                q1 = l1;
                q2 = l2;
                return;
            }

            var relative = start / stop;
            SplitQuadratic(l0, l1, l2, relative, out _, out _, out _, out q0, out q1, out q2);
        }

        private static void SplitQuadratic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            float t,
            out Vector2 l0,
            out Vector2 l1,
            out Vector2 l2,
            out Vector2 r0,
            out Vector2 r1,
            out Vector2 r2)
        {
            var p01 = Vector2.Lerp(p0, p1, t);
            var p12 = Vector2.Lerp(p1, p2, t);
            var p012 = Vector2.Lerp(p01, p12, t);
            l0 = p0;
            l1 = p01;
            l2 = p012;
            r0 = p012;
            r1 = p12;
            r2 = p2;
        }

        private static void SliceCubic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            float start,
            float stop,
            out Vector2 q0,
            out Vector2 q1,
            out Vector2 q2,
            out Vector2 q3)
        {
            SplitCubic(
                p0,
                p1,
                p2,
                p3,
                stop,
                out var l0,
                out var l1,
                out var l2,
                out var l3,
                out _,
                out _,
                out _,
                out _);
            if (start <= Epsilon)
            {
                q0 = l0;
                q1 = l1;
                q2 = l2;
                q3 = l3;
                return;
            }

            var relative = start / stop;
            SplitCubic(
                l0,
                l1,
                l2,
                l3,
                relative,
                out _,
                out _,
                out _,
                out _,
                out q0,
                out q1,
                out q2,
                out q3);
        }

        private static void SplitCubic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            float t,
            out Vector2 l0,
            out Vector2 l1,
            out Vector2 l2,
            out Vector2 l3,
            out Vector2 r0,
            out Vector2 r1,
            out Vector2 r2,
            out Vector2 r3)
        {
            var p01 = Vector2.Lerp(p0, p1, t);
            var p12 = Vector2.Lerp(p1, p2, t);
            var p23 = Vector2.Lerp(p2, p3, t);
            var p012 = Vector2.Lerp(p01, p12, t);
            var p123 = Vector2.Lerp(p12, p23, t);
            var p0123 = Vector2.Lerp(p012, p123, t);
            l0 = p0;
            l1 = p01;
            l2 = p012;
            l3 = p0123;
            r0 = p0123;
            r1 = p123;
            r2 = p23;
            r3 = p3;
        }

        private static bool TrySliceConic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            float weight,
            float start,
            float stop,
            out Vector2 q0,
            out Vector2 q1,
            out Vector2 q2,
            out float resultWeight)
        {
            var h0 = new Vector3(p0, 1f);
            var h1 = new Vector3(p1 * weight, weight);
            var h2 = new Vector3(p2, 1f);
            SplitConic(h0, h1, h2, stop, out var l0, out var l1, out var l2, out _, out _, out _);
            Vector3 r0;
            Vector3 r1;
            Vector3 r2;
            if (start <= Epsilon)
            {
                r0 = l0;
                r1 = l1;
                r2 = l2;
            }
            else
            {
                SplitConic(
                    l0,
                    l1,
                    l2,
                    start / stop,
                    out _,
                    out _,
                    out _,
                    out r0,
                    out r1,
                    out r2);
            }

            q0 = default;
            q1 = default;
            q2 = default;
            resultWeight = 1f;
            if (MathF.Abs(r0.Z) <= Epsilon ||
                MathF.Abs(r1.Z) <= Epsilon ||
                MathF.Abs(r2.Z) <= Epsilon ||
                r0.Z * r2.Z <= 0f)
            {
                return false;
            }

            q0 = new Vector2(r0.X, r0.Y) / r0.Z;
            q1 = new Vector2(r1.X, r1.Y) / r1.Z;
            q2 = new Vector2(r2.X, r2.Y) / r2.Z;
            resultWeight = r1.Z / MathF.Sqrt(r0.Z * r2.Z);
            return float.IsFinite(resultWeight) &&
                   float.IsFinite(q0.X) &&
                   float.IsFinite(q0.Y) &&
                   float.IsFinite(q1.X) &&
                   float.IsFinite(q1.Y) &&
                   float.IsFinite(q2.X) &&
                   float.IsFinite(q2.Y);
        }

        private static void SplitConic(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            float t,
            out Vector3 l0,
            out Vector3 l1,
            out Vector3 l2,
            out Vector3 r0,
            out Vector3 r1,
            out Vector3 r2)
        {
            var p01 = Vector3.Lerp(p0, p1, t);
            var p12 = Vector3.Lerp(p1, p2, t);
            var p012 = Vector3.Lerp(p01, p12, t);
            l0 = p0;
            l1 = p01;
            l2 = p012;
            r0 = p012;
            r1 = p12;
            r2 = p2;
        }
    }
}
