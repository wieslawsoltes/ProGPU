using System;
using ProGPU.Vector;

namespace ProGPU.Scene;

internal readonly record struct LinearDashCoverage(
    PathGeometry Path, Pen? Pen, RenderCommandGeometryCache GeometryCache);

public sealed partial class RenderCommandGeometryCache
{
    private bool? _linearStrokeTopology;
    private bool _linearDashPrepared, _linearDashSucceeded;
    private LinearDashKey _linearDashKey;
    private LinearDashCoverage _linearDashCoverage;

    internal static bool IsLinearDashCandidate(Pen pen) => pen.HasDashPattern &&
        !pen.IsHairline && pen.StrokeTransformMode == PenStrokeTransformMode.Normal;

    internal bool SupportsLinearDashCoverage(Pen pen)
    {
        if (!IsLinearDashCandidate(pen) || StrokePath == null)
            return false;
        if (_linearStrokeTopology.HasValue) return _linearStrokeTopology.Value;
        bool linear = !StrokePath.IsCombined;
        for (int f = 0; linear && f < StrokePath.Figures.Count; f++)
        {
            var figure = StrokePath.Figures[f];
            if (figure == null) { linear = false; break; }
            for (int i = 0; i < figure.Segments.Count; i++)
                if (figure.Segments[i] is not LineSegment) { linear = false; break; }
        }
        _linearStrokeTopology = linear;
        return linear;
    }

    // Source geometry is immutable for this cache's lifetime, as for the other
    // retained geometry entries. Paint identity is not an outline geometry key.
    internal bool TryGetLinearDashCoverage(Pen pen, float localThickness, out LinearDashCoverage coverage)
    {
        coverage = default;
        if (!SupportsLinearDashCoverage(pen)) return false;
        var key = new LinearDashKey(localThickness, pen.LineJoin, pen.MiterLimit,
            pen.StartLineCap, pen.EndLineCap, pen.DashCap, pen.DashOffset, pen.DashArrayStorage);
        if (!_linearDashPrepared || !_linearDashKey.Matches(key))
        {
            _linearDashPrepared = true;
            _linearDashSucceeded = false;
            _linearDashKey = key;
            _linearDashCoverage = default;
            // One bounded snapshot on geometry-key change; WithBrush shares
            // immutable dash storage instead of cloning the public array.
            var localPen = pen.WithBrush(pen.Brush);
            localPen.Thickness = localThickness;
            if (!StrokeCoverageGeometry.TryPrepareLinearPath(StrokePath!, localPen,
                    out var spine, out var preparedPen, out _, out var fill)) return false;
            _linearDashCoverage = fill == null
                ? new(spine, preparedPen, ForStrokePath(spine))
                : new(fill, null, ForPath(fill));
            _linearDashSucceeded = true;
        }
        if (!_linearDashSucceeded) return false;
        if (_linearDashCoverage.Pen is { } previous && !ReferenceEquals(previous.Brush, pen.Brush))
            _linearDashCoverage = _linearDashCoverage with { Pen = previous.WithBrush(pen.Brush) };
        coverage = _linearDashCoverage;
        return true;
    }

    private readonly record struct LinearDashKey(float Width, PenLineJoin Join, float Miter,
        PenLineCap Start, PenLineCap End, PenLineCap Dash, double Offset, double[]? Intervals)
    {
        // Equal NaN-valued invalid keys must retain their cached failure too.
        public bool Matches(LinearDashKey other) => Width.Equals(other.Width) && Join == other.Join
            && Miter.Equals(other.Miter) && Start == other.Start && End == other.End && Dash == other.Dash
            && Offset.Equals(other.Offset) && (ReferenceEquals(Intervals, other.Intervals)
                || Intervals.AsSpan().SequenceEqual(other.Intervals));
    }
}
