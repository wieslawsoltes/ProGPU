using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public enum SourceHitTestGeometryKind
{
    None,
    Excluded,
    Line,
    Rectangle,
    RoundedRectangle,
    Ellipse,
    PointRectangleBegin,
    PointRectangleEnd,
    PointEmptyBegin
}

/// <summary>Unsnapped source geometry for a recorded primitive. Raster fields remain authoritative for drawing.</summary>
public readonly record struct SourceHitTestGeometry(SourceHitTestGeometryKind Kind, Vector4 Coordinates)
{
    public static SourceHitTestGeometry Excluded => new(SourceHitTestGeometryKind.Excluded, default);

    internal RenderCommand Apply(in RenderCommand raster)
    {
        var result = raster;
        result.SourceHitGeometry = default;
        result.GeometryCache = null; // a raster-derived path is not the source path
        var c = Coordinates;
        if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W))
            throw new NotSupportedException("Source input geometry must be finite.");
        switch (Kind)
        {
            // Auxiliary raster caps are covered by the primary source line and its pen.
            case SourceHitTestGeometryKind.Excluded when raster.Type is RenderCommandType.DrawEllipse or RenderCommandType.FillTriangle:
                break;
            case SourceHitTestGeometryKind.Line when raster.Type == RenderCommandType.DrawLine:
                result.Position = new(c.X, c.Y);
                result.Position2 = new(c.Z, c.W);
                break;
            case SourceHitTestGeometryKind.Rectangle when raster.Type == RenderCommandType.DrawRect:
            case SourceHitTestGeometryKind.RoundedRectangle when raster.Type == RenderCommandType.DrawRoundedRect:
                if (c.Z < 0 || c.W < 0)
                    throw new NotSupportedException("Source rectangle extents must be nonnegative.");
                result.Rect = new Rect(c.X, c.Y, c.Z, c.W);
                break;
            case SourceHitTestGeometryKind.Ellipse when raster.Type == RenderCommandType.DrawEllipse:
                if (c.Z < 0 || c.W < 0)
                    throw new NotSupportedException("Source ellipse radii must be nonnegative.");
                result.Position2 = new(c.X, c.Y);
                result.RadiusX = c.Z;
                result.RadiusY = c.W;
                break;
            default:
                throw new NotSupportedException("Source input geometry does not match its raster primitive.");
        }
        return result;
    }
}
